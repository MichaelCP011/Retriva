using System.Net.Http.Headers;
using System.Security.Cryptography;
using Retriva.Shared.Config;
using Retriva.Shared.Security;

namespace Retriva.Agent;

/// <summary>Hasil satu unduhan, apa pun akhirnya.</summary>
public sealed record DownloadResult(bool Success, string Detail, string? SavedPath);

/// <summary>
/// Mengunduh berkas dari Superadmin, memverifikasinya, lalu menyimpannya di
/// folder mahasiswa.
///
/// Sertifikat diverifikasi dengan penyematan sidik jari yang sama seperti
/// sambungan SignalR. Ini bukan pengulangan yang bisa dilewat: HttpClient punya
/// jalur validasi sendiri, dan memakai yang bawaan berarti unduhan menerima
/// sertifikat apa pun yang ditandatangani CA mana pun — sementara seluruh model
/// kepercayaan kita justru dibangun di atas satu sertifikat yang tidak
/// ditandatangani siapa-siapa.
/// </summary>
public sealed class FileDownloader(
    AgentConfig config,
    ILogger log,
    string? userDocumentsDir = null,
    string? thumbprintOverride = null,
    string? unitId = null)
{
    /// <summary>
    /// Folder tujuan bawaan bila tidak ada pengguna interaktif.
    /// Memakai Public Documents agar dapat dibaca semua pengguna dan tidak terjebak di Session 0 (SYSTEM).
    /// </summary>
    public static string DefaultInboxDir
    {
        get
        {
            var commonDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (!string.IsNullOrWhiteSpace(commonDocs))
                return Path.Combine(commonDocs, "Retriva");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Retriva");
        }
    }

    /// <summary>
    /// Folder tujuan yang benar-benar dipakai: konfigurasi admin > dokumen user mahasiswa > public documents.
    /// </summary>
    public static string ResolveInbox(string? configured, string? userDocumentsDir = null, ILogger? log = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var full = Path.GetFullPath(configured.Trim());
                if (!Path.IsPathRooted(full)) throw new ArgumentException("bukan jalur absolut");

                Directory.CreateDirectory(full);
                return full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                log?.LogWarning("Folder berkas '{Dir}' tidak dapat dipakai ({Reason})", configured, ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(userDocumentsDir))
        {
            try
            {
                var userInbox = Path.Combine(userDocumentsDir, "Retriva");
                Directory.CreateDirectory(userInbox);
                return userInbox;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                log?.LogWarning("Folder dokumen mahasiswa '{Dir}' tidak dapat diakses ({Reason})",
                    userDocumentsDir, ex.Message);
            }
        }

        var fallback = DefaultInboxDir;
        try { Directory.CreateDirectory(fallback); } catch { /* best effort */ }
        return fallback;
    }

    /// <summary>Folder tujuan menurut konfigurasi agent yang sedang berjalan.</summary>
    public string InboxDir => ResolveInbox(config.FileInboxDirectory, userDocumentsDir, log);

    /// <summary>
    /// <paramref name="onProgress"/> dipanggil dengan jumlah byte yang sudah
    /// diterima, dibatasi laju di pemanggil supaya tidak membanjiri hub.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string url, string fileName, long expectedSize, string expectedSha256,
        Action<long> onProgress, CancellationToken ct)
    {
        var pin = !string.IsNullOrWhiteSpace(thumbprintOverride)
            ? thumbprintOverride
            : config.ConsoleCertificateThumbprint;

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                (_, certificate, _, _) => string.IsNullOrWhiteSpace(pin) || CertificatePin.Matches(certificate, pin),
        };

        using var client = new HttpClient(handler)
        {
            // Tanpa batas waktu keseluruhan. Berkas 2 GB di jaringan yang
            // tersendat memang wajar memakan belasan menit, dan memutusnya di
            // tengah jalan hanya menghasilkan pengulangan yang sama lambatnya.
            // Kemacetan sesungguhnya tertangkap oleh token pembatalan pemanggil.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!string.IsNullOrWhiteSpace(config.EnrollmentSecret))
            client.DefaultRequestHeaders.Add("X-Retriva-Enrollment", config.EnrollmentSecret);

        if (!string.IsNullOrWhiteSpace(config.DeviceToken))
            client.DefaultRequestHeaders.Add("X-Retriva-Token", config.DeviceToken);

        if (!string.IsNullOrWhiteSpace(unitId))
            client.DefaultRequestHeaders.Add("X-Retriva-Unit", unitId);

        Directory.CreateDirectory(InboxDir);

        // Diunduh ke berkas sementara lebih dulu.
        //
        // Menulis langsung ke nama tujuan berarti unduhan yang gagal di tengah
        // meninggalkan berkas separuh yang namanya benar — dan mahasiswa akan
        // membukanya, mendapati rusak, lalu melapor bahwa "berkasnya korup".
        // Berkas hanya muncul dengan nama sebenarnya setelah sidiknya cocok.
        var finalPath = UniquePath(Path.Combine(InboxDir, SafeName(fileName)));
        var tempPath = finalPath + ".part";

        try
        {
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult(false,
                    $"server menolak ({(int)response.StatusCode} {response.ReasonPhrase})", null);
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(tempPath))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    onProgress(total);
                }
            }

            var actual = await HashAsync(tempPath, ct);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                // Sidik yang tidak cocok berarti isinya berbeda dari yang
                // dikirim dosen. Menyimpannya tetap lebih buruk daripada gagal:
                // berkas yang salah tetapi tampak benar akan dipakai mengerjakan
                // tugas, dan kesalahannya baru ketahuan saat dinilai.
                Delete(tempPath, log);
                log.LogWarning("Sidik berkas tidak cocok untuk {File}: {Actual} bukan {Expected}",
                    fileName, actual[..16], expectedSha256[..Math.Min(16, expectedSha256.Length)]);
                return new DownloadResult(false, "sidik SHA-256 tidak cocok — berkas dibuang", null);
            }

            var size = new FileInfo(tempPath).Length;
            if (expectedSize > 0 && size != expectedSize)
            {
                Delete(tempPath, log);
                return new DownloadResult(false, $"ukuran tidak cocok ({size} bukan {expectedSize})", null);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            log.LogInformation("Berkas diterima: {Path} ({Size} byte)", finalPath, size);
            return new DownloadResult(true, $"tersimpan di {finalPath}", finalPath);
        }
        catch (OperationCanceledException)
        {
            Delete(tempPath, log);
            return new DownloadResult(false, "dibatalkan", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Delete(tempPath, log);
            return new DownloadResult(false, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>
    /// Membuang berkas separuh setelah unduhan gagal.
    ///
    /// Kegagalannya DICATAT. Berkas separuh yang tertinggal di inbox terlihat
    /// sama persis dengan berkas utuh bagi mahasiswa yang membukanya — dan yang
    /// ia temukan adalah dokumen rusak tanpa satu pun keterangan mengapa.
    /// </summary>
    private static void Delete(string path, ILogger log)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Sisa unduhan gagal dibuang: {Path} — {Message}", path, ex.Message);
        }
    }

    /// <summary>
    /// Membuang komponen path dari nama berkas.
    ///
    /// Nama datang dari sisi lain jaringan. Nama seperti "..\..\Startup\x.exe"
    /// akan menulis ke folder Startup pengguna bila dipakai apa adanya, dan
    /// pembagian materi kuliah berubah jadi jalur pemasangan program.
    /// </summary>
    private static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return string.IsNullOrWhiteSpace(name) ? "berkas-retriva" : name;
    }

    /// <summary>
    /// Menambah nomor bila nama sudah dipakai, alih-alih menimpa.
    ///
    /// Dosen sering mengirim ulang berkas dengan nama sama setelah memperbaiki
    /// isinya. Menimpa berarti pekerjaan mahasiswa yang kebetulan bernama sama
    /// hilang tanpa peringatan.
    /// </summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
