using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retriva.Setup;

/// <summary>
/// Mengumpulkan seluruh log dan keadaan mesin ini menjadi SATU berkas zip.
///
/// ADA KARENA "KIRIMKAN LOGNYA" TERNYATA BUKAN PERMINTAAN YANG SEDERHANA.
///
/// Log Retriva tersebar di beberapa berkas, dan yang menjalankannya berbeda
/// akun: panel berjalan sebagai dosen, agent sebagai LocalSystem, klien sebagai
/// mahasiswa. Menyebut jalurnya satu per satu berarti menyuruh orang membuka
/// tiga folder tersembunyi di tiga mesin berbeda dan menebak mana yang relevan.
///
/// Lebih buruk lagi, log saja jarang cukup. Pertanyaan yang berulang muncul —
/// layanannya jalan tidak, aturan firewallnya ada tidak, alamat IP-nya berapa,
/// versinya berapa — dijawab oleh keadaan sistem, bukan oleh log, dan meminta
/// keempatnya lewat percakapan memakan beberapa putaran bolak-balik yang
/// masing-masing berjarak satu jam.
///
/// Satu perintah, satu berkas, satu kali kirim.
///
/// RAHASIA DIBUANG SEBELUM DIBUNGKUS. Kode pendaftaran dan token perangkat
/// disamarkan, dan console.pfx — sertifikat berikut kunci privat lab — tidak
/// pernah ikut. Berkas ini dikirim lewat WhatsApp atau surel oleh orang yang
/// sedang panik, dan bundel diagnosa yang membocorkan kunci lab adalah harga
/// yang tidak sepadan dengan kemudahannya.
/// </summary>
public static class LogBundle
{
    /// <summary>Kunci JSON yang isinya tidak boleh ikut apa adanya.</summary>
    private static readonly string[] Secrets =
    [
        "enrollmentSecret",
        "agentEnrollmentSecret",
        "deviceToken",
    ];

    /// <summary>
    /// Menyusun bundelnya. Mengembalikan jalur zip yang dihasilkan.
    /// </summary>
    public static string Collect(Action<string> log)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = $"retriva-diagnosa-{Environment.MachineName}-{stamp}";

        var staging = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(staging);

        try
        {
            CopyLogs(staging, log);
            CopyConfigs(staging, log);
            WriteSystemState(staging, log);

            /*
                Zip ditulis ke Desktop, bukan ke Documents atau folder installer.

                Orang yang baru saja menjalankan perintah ini perlu menemukan
                hasilnya tanpa diberi tahu, dan Desktop adalah satu-satunya
                tempat yang sudah terbuka di depannya. Documents menuntut membuka
                Explorer dan mencari; folder installer di Program Files bahkan
                tidak dapat ditulis oleh proses tak terelevasi.
            */
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                desktop = Path.GetTempPath();

            var zip = Path.Combine(desktop, name + ".zip");
            if (File.Exists(zip)) File.Delete(zip);

            ZipFile.CreateFromDirectory(staging, zip, CompressionLevel.Optimal, false);
            log($"Bundel dibuat: {zip}");
            return zip;
        }
        finally
        {
            // Folder sementara dibersihkan apa pun hasilnya. Isinya salinan
            // konfigurasi, dan meninggalkannya di Temp berarti menyebarkan
            // salinan kedua yang tidak diketahui siapa pun.
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { /* sudah cukup; zip-nya yang penting */ }
        }
    }

    private static void CopyLogs(string staging, Action<string> log)
    {
        var source = Path.Combine(SetupProfile.DataDir, "logs");
        var target = Path.Combine(staging, "logs");
        Directory.CreateDirectory(target);

        var copied = 0;

        if (Directory.Exists(source))
        {
            foreach (var file in Directory.GetFiles(source, "*.log"))
            {
                try
                {
                    /*
                        Dibuka dengan FileShare.ReadWrite, bukan File.Copy.

                        Berkas log sedang dipegang proses yang menulisnya —
                        agent berjalan terus, panel mungkin terbuka. File.Copy
                        akan gagal dengan "sedang dipakai proses lain" pada
                        persis berkas yang paling dibutuhkan, yaitu milik
                        komponen yang sedang bermasalah.
                    */
                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var dst = File.Create(Path.Combine(target, Path.GetFileName(file)));
                    src.CopyTo(dst);
                    copied++;
                }
                catch (IOException ex)
                {
                    log($"  {Path.GetFileName(file)} tidak terbaca: {ex.Message}");
                }
            }
        }

        // Log lama di %TEMP% ikut diambil bila masih ada. Versi sebelum
        // penyatuan folder menulis ke sana, dan mesin yang baru diperbarui
        // masih menyimpan jejak masalah yang justru sedang ditelusuri.
        foreach (var legacy in new[] { "retriva-console.log", "retriva-panel.log", "retriva-client.log" })
        {
            var path = Path.Combine(Path.GetTempPath(), legacy);
            if (!File.Exists(path)) continue;

            try
            {
                using var src = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(Path.Combine(target, "lama-" + legacy));
                src.CopyTo(dst);
                copied++;
            }
            catch (IOException) { /* tidak penting; ini hanya sisa versi lama */ }
        }

        log(copied == 0
            ? "Tidak ada berkas log ditemukan — komponennya mungkin belum pernah berjalan."
            : $"{copied} berkas log disalin.");
    }

    private static void CopyConfigs(string staging, Action<string> log)
    {
        var target = Path.Combine(staging, "konfigurasi");
        Directory.CreateDirectory(target);

        foreach (var file in new[] { "agent.json", "panel.json", "console.json" })
        {
            var path = Path.Combine(SetupProfile.DataDir, file);
            if (!File.Exists(path)) continue;

            try
            {
                File.WriteAllText(Path.Combine(target, file), Redact(File.ReadAllText(path)));
                log($"  {file} disalin (rahasia disamarkan)");
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                log($"  {file} tidak terbaca: {ex.Message}");
            }
        }

        // console.pfx dan devices.json SENGAJA tidak ikut. Yang pertama berisi
        // kunci privat lab; yang kedua berisi token setiap perangkat terdaftar.
        // Keduanya tidak menjawab satu pun pertanyaan diagnosa.
    }

    /// <summary>
    /// Mengganti isi medan rahasia dengan penanda panjangnya.
    ///
    /// Panjangnya DIPERTAHANKAN karena ia menjawab pertanyaan yang nyata: medan
    /// kosong dan medan terisi adalah dua masalah yang sama sekali berbeda, dan
    /// menyamarkan keduanya menjadi "(disamarkan)" akan menghapus perbedaan itu.
    /// </summary>
    private static string Redact(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject root) return json;

        foreach (var key in Secrets)
        {
            if (!root.ContainsKey(key)) continue;

            var value = root[key]?.GetValue<string>();
            root[key] = string.IsNullOrEmpty(value)
                ? "(KOSONG)"
                : $"(disamarkan · {value.Length} karakter)";
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteSystemState(string staging, Action<string> log)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Mesin        : {Environment.MachineName}");
        sb.AppendLine($"Pengguna     : {Environment.UserName}");
        sb.AppendLine($"Windows      : {Environment.OSVersion}");
        sb.AppendLine($"Waktu        : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Versi Retriva: {SetupProfile.Version}");
        sb.AppendLine($"Folder data  : {SetupProfile.DataDir}");
        sb.AppendLine();

        sb.AppendLine("Terpasang:");
        foreach (var role in new[] { SetupRole.Lab, SetupRole.Panel })
        {
            var profile = new SetupProfile { Role = role };
            var dir = profile.DefaultInstallDir;

            // Dihitung ke variabel lebih dulu, bukan disisipkan sebagai
            // ekspresi bersyarat di dalam string. Interpolasi yang isinya
            // melintasi baris memang legal sejak C# 11, tetapi hasilnya adalah
            // baris yang tidak dapat dipindai dengan mata dan tidak dapat
            // dibaca alat bantu mana pun dengan benar.
            var state = Directory.Exists(dir) ? dir : "tidak terpasang di folder bawaan";

            sb.AppendLine($"  {profile.ProductName,-28} {state}");
        }
        sb.AppendLine();

        /*
            Perintah sistem dijalankan dan keluarannya disalin apa adanya.

            Empat pertanyaan yang selalu muncul saat sebuah unit tidak menyambung
            — layanannya jalan, firewallnya terbuka, alamat IP-nya berapa,
            koordinatornya terjangkau — semuanya dijawab di sini tanpa satu pun
            putaran tanya-jawab.
        */
        foreach (var (title, exe, args) in new[]
        {
            ("Layanan RetrivaAgent", "sc", "query RetrivaAgent"),
            ("Aturan firewall Retriva", "netsh", "advfirewall firewall show rule name=all dir=in"),
            ("Alamat jaringan", "ipconfig", "/all"),
            ("Porta yang didengarkan", "netstat", "-ano -p tcp"),
        })
        {
            sb.AppendLine(new string('=', 70));
            sb.AppendLine(title);
            sb.AppendLine(new string('=', 70));
            sb.AppendLine(Run(exe, args, title.Contains("firewall") ? "Retriva" : null));
            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(staging, "keadaan-sistem.txt"), sb.ToString());
        log("Keadaan sistem dicatat.");
    }

    /// <summary>
    /// Menjalankan satu perintah dan mengembalikan keluarannya.
    ///
    /// <paramref name="filter"/> menyaring baris yang mengandung kata itu, untuk
    /// keluaran yang panjangnya ribuan baris. Daftar seluruh aturan firewall
    /// Windows membanjiri berkasnya sampai bagian Retriva-nya tidak dapat
    /// ditemukan, dan bagian itulah satu-satunya yang ditanyakan.
    /// </summary>
    private static string Run(string exe, string args, string? filter)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return "(tidak dapat dijalankan)";

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            // Batas waktu supaya perintah yang menggantung tidak menahan
            // seluruh pengumpulan. netstat pada mesin sibuk bisa lambat.
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return "(perintah tidak selesai dalam 20 detik)";
            }

            var text = output + error;

            if (filter is not null)
            {
                var lines = text.Split('\n')
                    .Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                text = lines.Length == 0
                    ? $"(tidak ada baris yang menyebut \"{filter}\")"
                    : string.Join('\n', lines);
            }

            return text.Trim();
        }
        catch (Exception ex)
        {
            return $"(gagal: {ex.Message})";
        }
    }
}
