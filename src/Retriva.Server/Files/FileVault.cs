using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Retriva.Shared;

namespace Retriva.Server.Files;

/// <summary>Satu berkas yang siap diunduh agent.</summary>
public sealed record StagedFile(
    string Id, string FileName, long SizeBytes, string Sha256, DateTimeOffset StagedAt);

/// <summary>
/// Menyimpan berkas yang akan dibagikan, dan menyajikannya lewat HTTP.
///
/// Modelnya TARIK, bukan dorong. Berkas dititipkan sekali ke sini, lalu tiap
/// agent mengunduhnya sendiri. Alternatifnya — panel mendorong potongan ke 40
/// agent sekaligus — punya tiga kelemahan yang semuanya baru terasa di lab
/// sungguhan: satu PC yang lambat menahan 39 lainnya, sambungan yang putus
/// berarti mengulang dari awal, dan panel harus melayani 40 aliran serentak
/// sambil tetap menangkap layar untuk siaran.
///
/// Salinannya disimpan di folder terpisah, bukan dibaca langsung dari lokasi
/// asal. Kalau dibaca dari lokasi asal, dosen yang memindahkan atau menyunting
/// berkasnya di tengah pembagian akan membuat sebagian unit menerima isi yang
/// berbeda dari sebagian lainnya — dan tidak ada yang akan menyadarinya.
/// </summary>
public sealed class FileVault
{
    private readonly Dictionary<string, StagedFile> _staged = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ILogger<FileVault>? _log;

    public static string VaultDir => Path.Combine(RetrivaPaths.DataDir, "files");

    /// <summary>
    /// Logger bersifat opsional karena panel non-Superadmin membuat lemari ini
    /// tanpa wadah DI — ia hanya perlu bentuk yang sama, tidak pernah memakainya.
    /// </summary>
    public FileVault(ILogger<FileVault>? log = null)
    {
        _log = log;
        Directory.CreateDirectory(VaultDir);
    }

    /// <summary>
    /// Menyalin berkas ke lemari sambil menghitung sidik SHA-256-nya.
    ///
    /// SATU LINTASAN, bukan dua — dan itu bukan penghematan yang bersifat
    /// kosmetik.
    ///
    /// Versi pertama menyalin berkasnya lebih dulu dengan File.Copy, lalu
    /// membuka salinannya dan membacanya ulang dari awal untuk dihitung
    /// sidiknya. Berkas 7 GB berarti 7 GB dibaca, 7 GB ditulis, lalu 7 GB
    /// dibaca lagi: 21 GB lalu lintas disk untuk satu berkas.
    ///
    /// Sekarang potongan yang baru dibaca langsung diumpankan ke penghitung
    /// sidik sebelum ditulis, jadi lintasan keduanya hilang sama sekali.
    ///
    /// Sifat penting dari versi lama tetap dipertahankan, bahkan diperkuat:
    /// sidik yang dihasilkan adalah sidik dari BYTE YANG BENAR-BENAR DITULIS ke
    /// lemari, bukan dari berkas asal yang mungkin berubah sesudahnya. Agent
    /// yang mengunduh dan memverifikasi tidak akan pernah menolak berkas yang
    /// sebenarnya utuh.
    ///
    /// Progres dilaporkan lewat <paramref name="onProgress"/> karena berkas
    /// besar butuh menit-menitan di sini, dan penyalinan tanpa kabar apa pun
    /// tidak dapat dibedakan dari aplikasi yang menggantung.
    /// </summary>
    public async Task<StagedFile> StageAsync(
        string sourcePath,
        Action<long, long>? onProgress = null,
        CancellationToken token = default)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var target = Path.Combine(VaultDir, id);
        var total = new FileInfo(sourcePath).Length;

        long copied = 0;
        string hash;

        try
        {
            /*
                Buffer 1 MB, dan FileOptions.SequentialScan.

                Bawaan FileStream adalah 4 KB, yang untuk berkas 7 GB berarti
                hampir dua juta panggilan baca. Petunjuk SequentialScan memberi
                tahu Windows bahwa berkas ini dibaca lurus dari awal ke akhir,
                sehingga cache-nya tidak menyimpan halaman yang sudah lewat dan
                tidak menggusur sisa isi memori mesin asisten.
            */
            const int chunk = 1024 * 1024;

            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var sink = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None,
                chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[chunk];
            var lastReport = 0L;

            while (true)
            {
                var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;

                digest.AppendData(buffer, 0, read);
                await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

                copied += read;

                // Dikabarkan tiap 16 MB, bukan tiap potongan. Empat puluh ribu
                // pesan lintas-proses untuk satu berkas akan membebani jembatan
                // WebView lebih berat daripada penyalinannya sendiri.
                if (copied - lastReport >= 16L * 1024 * 1024 || copied == total)
                {
                    lastReport = copied;
                    onProgress?.Invoke(copied, total);
                }
            }

            hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            /*
                Salinan separuh jadi DIBUANG.

                Tanpa ini, pembatalan atau disk penuh meninggalkan berkas
                bernama id yang sah di dalam folder lemari. Ia tidak terdaftar,
                jadi tidak akan pernah disajikan maupun dibersihkan Clear() —
                tujuh gigabyte yang menempati disk PC Asisten tanpa satu pun
                bagian program yang tahu ia ada.
            */
            try { if (File.Exists(target)) File.Delete(target); }
            catch (IOException) { /* biarkan; kegagalan aslinya lebih penting */ }
            throw;
        }

        var staged = new StagedFile(
            id, Path.GetFileName(sourcePath), copied, hash, DateTimeOffset.UtcNow);

        lock (_gate) _staged[id] = staged;
        _log?.LogInformation("Berkas dititipkan: {Name} · {Size} bita · {Id}",
            staged.FileName, staged.SizeBytes, id);

        return staged;
    }

    public StagedFile? Find(string id)
    {
        lock (_gate) return _staged.GetValueOrDefault(id);
    }

    /// <summary>
    /// Membuka isi berkas untuk disajikan.
    ///
    /// Id diverifikasi terhadap daftar, bukan dipakai langsung menyusun path.
    /// Tanpa itu, permintaan berisi "../console.pfx" akan menyajikan sertifikat
    /// dan kunci privat konsol kepada siapa pun yang bisa menebak namanya.
    /// </summary>
    public FileStream? Open(string id)
    {
        if (Find(id) is null) return null;

        var path = Path.Combine(VaultDir, id);
        return File.Exists(path)
            ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    public IReadOnlyList<StagedFile> All()
    {
        lock (_gate) return _staged.Values.OrderByDescending(f => f.StagedAt).ToList();
    }

    /// <summary>
    /// Membuang satu berkas dari lemari beserta salinannya di disk.
    /// Dipanggil setelah pembagian selesai atau saat sesi kelas direset.
    /// </summary>
    public void Remove(string id)
    {
        lock (_gate) _staged.Remove(id);
        try { File.Delete(Path.Combine(VaultDir, id)); }
        catch (IOException) { /* berkas mungkin sedang dibaca agent */ }
    }

    /// <summary>
    /// Mengosongkan lemari. Materi kelas sebelumnya tidak boleh tersedia untuk
    /// diunduh kelas berikutnya hanya karena tidak ada yang membersihkannya.
    ///
    /// MELAPORKAN YANG GAGAL, bukan hanya yang diminta.
    ///
    /// Versi sebelumnya menelan IOException tanpa suara lalu mengembalikan
    /// jumlah penuh sebagai berhasil. Berkas yang gagal dihapus — karena sedang
    /// dibaca agent, atau karena hak akses — tetap ada di lemari dan tetap dapat
    /// diunduh. Persis yang dijanjikan komentar di atas tidak akan terjadi.
    ///
    /// Asisten menekan "Kosongkan", melihat "5 berkas dihapus", dan soal ujian
    /// kemarin masih di sana. Itu kebohongan pada topik yang paling tidak boleh
    /// dibohongi.
    /// </summary>
    public (int Deleted, int Failed) Clear()
    {
        List<StagedFile> entries;
        lock (_gate) { entries = [.. _staged.Values]; _staged.Clear(); }

        var deleted = 0;
        var failed = 0;

        foreach (var entry in entries)
        {
            try
            {
                File.Delete(Path.Combine(VaultDir, entry.Id));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;

                // Dikembalikan ke catatan. Berkas yang masih ada di disk tetapi
                // hilang dari catatan tidak akan pernah dicoba dihapus lagi —
                // dan tetap dapat diunduh selamanya oleh siapa pun yang tahu
                // id-nya.
                lock (_gate) _staged[entry.Id] = entry;

                _log?.LogError(ex,
                    "Gagal menghapus {File} dari lemari — berkas MASIH dapat diunduh", entry.FileName);
            }
        }

        return (deleted, failed);
    }
}
