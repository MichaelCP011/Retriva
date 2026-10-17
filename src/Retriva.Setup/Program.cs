using System.IO;
using System.Text.Json;
using System.Windows;

namespace Retriva.Setup;

/// <summary>
/// Titik masuk installer.
///
/// SATU BERKAS, TIGA PERAN: memasang, mencopot, dan memeriksa.
///
/// Yang ketiga bukan tambahan iseng. RetrivaSetup.exe ikut terpasang di folder
/// program, jadi ia dapat dijalankan berbulan-bulan kemudian untuk menjawab
/// "mengapa unit ini tidak menyambung" — tanpa memasang apa pun, tanpa mengubah
/// apa pun. Itu setengah jalan menuju aplikasi swadiagnosa yang direncanakan.
///
/// EXIT CODE:
///   0  berhasil, atau pemeriksaan lolos
///   1  dibatalkan pengguna
///   2  ada prasyarat yang menghalangi
///   3  pemasangan gagal dan sudah dibatalkan
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            var isUninstall = Has(args, "--uninstall")
                           || exeName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)
                           || exeName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase);

            if (isUninstall) return RunUninstall(args);
            if (Has(args, "--check")) return Check(args);
            if (Has(args, "--logs")) return CollectLogs(args);
            if (Has(args, "--reconfigure")) return Reconfigure(args);
            if (Has(args, "--silent")) return SilentInstall(args);

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new SetupWindow());
            return 0;
        }
        catch (Exception ex)
        {
            /*
                Pengecualian yang lolos sampai sini DITAMPILKAN, tidak hanya
                dikembalikan sebagai exit code.

                Installer yang tertutup sendiri tanpa pesan adalah kegagalan
                terburuk dari sudut pandang orang di depannya: tidak ada yang
                dapat dilaporkan, tidak ada yang dapat dicari. Pada pemasangan
                senyap tidak ada layar untuk menampilkannya, jadi ia menuju
                stderr — yang tertangkap oleh log skrip pemasangan.
            */
            var message = $"Installer berhenti: {ex.Message}";

            if (Has(args, "--silent") || Has(args, "--check"))
                Console.Error.WriteLine(message);
            else
                MessageBox.Show(message, "Retriva", MessageBoxButton.OK, MessageBoxImage.Error);

            return 3;
        }
    }

    /* ================= mode tanpa jendela ================= */

    private static int Check(string[] args)
    {
        var host = Value(args, "--host") ?? "";
        var port = int.TryParse(Value(args, "--port"), out var p) ? p : 8443;

        var checks = Prerequisites.RunAll(host, port);
        foreach (var c in checks) Console.WriteLine($"[{c.State,-7}] {c.Title}: {c.Detail}");

        var blocking = checks.Count(c => c.State == CheckState.Fail);
        if (blocking == 0) return 0;

        Console.WriteLine($"GAGAL: {blocking} prasyarat menghalangi pemasangan.");
        return 2;
    }

    /// <summary>
    /// Mengumpulkan log dan keadaan mesin menjadi satu zip di Desktop.
    ///
    /// TIDAK MENUNTUT HAK ADMINISTRATOR untuk hal yang dapat dibaca tanpa itu,
    /// dan tidak mengubah apa pun. Ia dapat dijalankan mahasiswa di PC-nya
    /// sendiri, oleh dosen di laptopnya, atau oleh asisten di PC Superadmin —
    /// perintahnya sama di ketiga mesin, dan itulah gunanya.
    /// </summary>
    private static int CollectLogs(string[] args)
    {
        var log = Logger(args);
        var quiet = Has(args, "--silent");

        try
        {
            var zip = LogBundle.Collect(log);

            if (!quiet)
            {
                /*
                    Hasilnya DITAMPILKAN, bukan hanya ditulis ke konsol.

                    RetrivaSetup adalah WinExe: dijalankan lewat klik ganda pada
                    pintasan, ia tidak punya konsol sama sekali. Tanpa dialog
                    ini, orang yang menekan "Kumpulkan log" melihat jendela
                    berkedip sesaat lalu tidak terjadi apa-apa — dan tidak punya
                    cara mengetahui bahwa berkasnya sudah ada di Desktop.
                */
                MessageBox.Show(
                    $"Bundel diagnosa selesai dibuat.\n\n{zip}\n\n"
                  + "Berkas ini berisi log, konfigurasi (kode pendaftaran dan token "
                  + "sudah disamarkan), dan keadaan jaringan mesin ini. Kirimkan "
                  + "berkas zip tersebut apa adanya.",
                    "Retriva", MessageBoxButton.OK, MessageBoxImage.Information);

                // Explorer dibuka pada berkasnya, supaya tidak perlu dicari.
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{zip}\"",
                        UseShellExecute = true,
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Explorer tidak terbuka bukan kegagalan; jalurnya sudah
                    // disebutkan di dialog.
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            var message = $"Pengumpulan log gagal: {ex.Message}";
            if (quiet) Console.Error.WriteLine(message);
            else MessageBox.Show(message, "Retriva", MessageBoxButton.OK, MessageBoxImage.Error);
            return 3;
        }
    }

    private static int SilentInstall(string[] args)
    {
        var roleArg = Value(args, "--role");
        var isPanel = string.Equals(roleArg, "panel", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(roleArg, "admin", StringComparison.OrdinalIgnoreCase);

        var isUpdate = Has(args, "--update");

        var profile = new SetupProfile
        {
            Role = isPanel ? SetupRole.Panel : SetupRole.Lab,
            Host = Value(args, "--host") ?? "192.168.1.252",
            Port = int.TryParse(Value(args, "--port"), out var p) ? p : 8443,
            EnrollmentSecret = Value(args, "--secret") ?? "",
            Thumbprint = Value(args, "--thumbprint") ?? "",
            HostsLab = Has(args, "--hosts-lab"),
            DisplayName = Value(args, "--name") ?? Environment.MachineName,
        };

        // Jika mode pembaruan (in-place update), baca konfigurasi lama agar tidak hilang
        var agentConfigFile = Path.Combine(SetupProfile.DataDir, "agent.json");
        if (profile.Role == SetupRole.Lab && File.Exists(agentConfigFile))
        {
            isUpdate = true;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(agentConfigFile));
                var root = doc.RootElement;
                if (string.IsNullOrWhiteSpace(profile.EnrollmentSecret) &&
                    root.TryGetProperty("enrollmentSecret", out var sec))
                {
                    profile.EnrollmentSecret = sec.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(profile.Thumbprint) &&
                    root.TryGetProperty("certificateThumbprint", out var thumb))
                {
                    profile.Thumbprint = thumb.GetString() ?? "";
                }
                if (root.TryGetProperty("consoleEndpoints", out var eps) && eps.ValueKind == JsonValueKind.Array && eps.GetArrayLength() > 0)
                {
                    profile.Host = eps[0].GetString() ?? profile.Host;
                }
                if (root.TryGetProperty("consolePort", out var portVal) && portVal.TryGetInt32(out var pVal))
                {
                    profile.Port = pVal;
                }
            }
            catch
            {
                // jika berkas konfigurasi lama bermasalah, lanjutkan alur biasa
            }
        }

        /*
            Kode pendaftaran dan sidik jari WAJIB pada pemasangan senyap baru peran Lab.
            Pada pembaruan (update), nilai lama dipertahankan sehingga tidak wajib diisi lagi.
        */
        if (profile.Role == SetupRole.Lab && !isUpdate)
        {
            if (string.IsNullOrWhiteSpace(profile.EnrollmentSecret))
            {
                Console.Error.WriteLine(
                    "GAGAL: --secret wajib diisi pada pemasangan senyap baru. "
                  + "Nilainya ada di layar Konfigurasi pada panel Superadmin.");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(profile.Thumbprint))
            {
                Console.Error.WriteLine(
                    "GAGAL: --thumbprint wajib diisi. Tanpa penyematan sertifikat, "
                  + "agent menolak menyambung ke koordinator mana pun.");
                return 2;
            }
        }

        if (!Payload.Has(profile.Role))
        {
            Console.Error.WriteLine(
                $"GAGAL: muatan untuk peran {profile.Role} tidak ada di dalam installer ini.");
            return 2;
        }

        var log = Logger(args);

        var checks = Prerequisites.RunAll(profile.Host, profile.Port, isUpdate);
        var blocking = checks.Where(c => c.State == CheckState.Fail).ToList();

        foreach (var c in checks) log($"[{c.State,-7}] {c.Title}: {c.Detail}");

        if (blocking.Count > 0)
        {
            log($"GAGAL: {blocking.Count} prasyarat menghalangi pemasangan.");
            return 2;
        }

        var failure = Installer.Build(profile, log).Run();

        if (failure is null)
        {
            log($"{profile.ProductName} terpasang di {profile.InstallDir}");
            return 0;
        }

        log($"GAGAL: {failure}");
        return 3;
    }

    /// <summary>
    /// Mengarahkan unit yang SUDAH TERPASANG ke lab lain, tanpa memasang ulang.
    ///
    /// PEMASANGAN ULANG ADALAH HARGA YANG SALAH UNTUK MENGUBAH SATU BARIS.
    ///
    /// Sebelum ini, satu-satunya cara mengubah alamat koordinator adalah
    /// menjalankan seluruh installer lagi — menghentikan layanan, menimpa
    /// ratusan megabyte berkas program, mendaftarkan ulang layanan dan aturan
    /// firewall — semuanya untuk memperbarui empat medan di satu berkas JSON.
    /// Dikalikan empat puluh PC, itu pekerjaan setengah hari untuk perpindahan
    /// ruangan yang sebenarnya tidak mengubah apa pun di mesinnya.
    ///
    /// Yang disentuh di sini hanya agent.json dan layanannya. Berkas program
    /// tidak dibuka sama sekali.
    ///
    /// TOKEN PERANGKAT SENGAJA DIPERTAHANKAN. Lab tujuan tidak mengenalinya dan
    /// akan meminta pendaftaran baru dengan kode yang diberikan — sementara lab
    /// ASAL tetap mengenali unit ini bila ia kembali, lengkap dengan riwayat
    /// percakapan, berkas, dan catatan ujiannya.
    /// </summary>
    private static int Reconfigure(string[] args)
    {
        var log = Logger(args);

        if (!Has(args, "--silent"))
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new ReconfigureWindow());
            return 0;
        }

        var host = Value(args, "--host");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("GAGAL: --host wajib diisi pada konfigurasi ulang senyap.");
            return 2;
        }

        var profile = new SetupProfile
        {
            Role = SetupRole.Lab,
            Host = host.Trim(),
            Port = int.TryParse(Value(args, "--port"), out var p) ? p : 8443,
            EnrollmentSecret = Value(args, "--secret") ?? "",
            Thumbprint = Value(args, "--thumbprint") ?? "",
            FileInboxDir = Value(args, "--files"),
        };

        /*
            Kode pendaftaran dan sidik jari WAJIB, sama seperti pemasangan senyap.

            Menulisnya kosong akan menghasilkan unit yang layanannya berjalan,
            konfigurasinya terlihat benar, dan setiap upaya mendaftarnya ditolak
            koordinator. Tidak ada satu pun galat yang sampai ke orang yang
            menjalankan skripnya.
        */
        if (string.IsNullOrWhiteSpace(profile.EnrollmentSecret))
        {
            Console.Error.WriteLine(
                "GAGAL: --secret wajib diisi. Nilainya ada di layar Konfigurasi "
              + "pada panel Superadmin lab TUJUAN, bukan lab asal.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(profile.Thumbprint))
        {
            Console.Error.WriteLine(
                "GAGAL: --thumbprint wajib diisi. Tanpa penyematan sertifikat, "
              + "agent menolak menyambung ke koordinator mana pun.");
            return 2;
        }

        return ApplyReconfigure(profile, log) ? 0 : 3;
    }

    /// <summary>
    /// Menulis agent.json lalu menghidupkan ulang layanan. Dipakai kedua mode.
    ///
    /// URUTANNYA MENENTUKAN: layanan dihentikan SEBELUM berkasnya ditulis.
    /// Agent membaca konfigurasi saat start dan memegangnya di memori, jadi
    /// menulis lebih dulu lalu me-restart menyisakan celah tempat agent masih
    /// berjalan dengan konfigurasi lama sementara berkasnya sudah berisi yang
    /// baru — dan bila restart-nya gagal, keadaan itu bertahan sampai ada yang
    /// menyadarinya.
    /// </summary>
    public static bool ApplyReconfigure(SetupProfile profile, Action<string> log)
    {
        try
        {
            log("Menghentikan layanan Retriva Agent...");
            SystemActions.StopService(SetupProfile.ServiceName, TimeSpan.FromSeconds(20));

            profile.WriteAgentConfig(log);

            log("Menjalankan layanan kembali...");
            SystemActions.StartService(SetupProfile.ServiceName);

            log($"Selesai. Unit ini sekarang menuju {profile.Host}:{profile.Port}.");
            log("Unit akan muncul di dashboard lab tujuan setelah asisten menyetujuinya.");
            return true;
        }
        catch (Exception ex)
        {
            /*
                Layanan DICOBA dihidupkan lagi walau penulisannya gagal.

                Kegagalan di tengah meninggalkan mesin dengan layanan yang mati
                dan konfigurasi yang mungkin separuh tertulis. Membiarkannya mati
                berarti unit itu hilang dari KEDUA lab — yang asal maupun yang
                tujuan — dan tidak ada apa pun di layar mana pun yang
                menjelaskannya.
            */
            log($"GAGAL: {ex.Message}");
            try
            {
                SystemActions.StartService(SetupProfile.ServiceName);
                log("Layanan dihidupkan kembali dengan konfigurasi yang ada.");
            }
            catch (Exception restart)
            {
                log($"Layanan TIDAK dapat dihidupkan kembali: {restart.Message}");
            }
            return false;
        }
    }

    private static int RunUninstall(string[] args)
    {
        var currentPath = Environment.ProcessPath ?? "";
        var role = currentPath.Contains(
            Path.Combine("Retriva", "Panel"), StringComparison.OrdinalIgnoreCase)
            ? SetupRole.Panel
            : SetupRole.Lab;

        if (Value(args, "--role") is { Length: > 0 } forced)
        {
            role = string.Equals(forced, "panel", StringComparison.OrdinalIgnoreCase)
                ? SetupRole.Panel
                : SetupRole.Lab;
        }

        var cleanData = Has(args, "--clean-data");

        // Jika mode senyap (--silent)
        if (Has(args, "--silent"))
        {
            var log = Logger(args);
            Installer.Uninstall(role, cleanData, log);
            return 0;
        }

        // Jika dipanggil langsung dari dalam folder Program Files (misal C:\Program Files\Retriva\Lab\Uninstall.exe),
        // salin sementara ke %TEMP% agar Uninstall.exe tidak mengunci folder saat menghapus Program Files!
        var inProgramFiles = currentPath.Contains(Path.Combine("Retriva", "Lab"), StringComparison.OrdinalIgnoreCase)
                          || currentPath.Contains(Path.Combine("Retriva", "Panel"), StringComparison.OrdinalIgnoreCase);

        if (inProgramFiles && !Has(args, "--from-temp"))
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "RetrivaUninstall");
                Directory.CreateDirectory(tempDir);
                var tempExe = Path.Combine(tempDir, "RetrivaUninstall.exe");
                File.Copy(currentPath, tempExe, overwrite: true);

                var passArgs = new List<string>(args) { "--from-temp", "--uninstall" };
                if (!Has(args, "--role")) passArgs.Add($"--role {(role == SetupRole.Panel ? "panel" : "lab")}");

                var psi = new System.Diagnostics.ProcessStartInfo(tempExe, string.Join(" ", passArgs))
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
                return 0;
            }
            catch
            {
                // Jika trampoline gagal, buka jendela langsung
            }
        }

        // Tampilkan jendela uninstaller kustom WPF Retriva
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new UninstallWindow(role));
        return 0;
    }

    /* ================= bantu ================= */

    /// <summary>
    /// Log ke konsol, dan ke berkas bila --log diberikan.
    ///
    /// Berkas log penting justru pada pemasangan senyap: tidak ada konsol yang
    /// dibaca siapa pun saat empat puluh PC dipasang lewat skrip, dan satu unit
    /// yang gagal harus dapat ditelusuri setelahnya.
    /// </summary>
    private static Action<string> Logger(string[] args)
    {
        var file = Value(args, "--log");
        if (string.IsNullOrWhiteSpace(file)) return Console.WriteLine;

        return line =>
        {
            Console.WriteLine(line);
            try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"); }
            catch (IOException) { /* berkas terkunci; konsol tetap menerima */ }
        };
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Menerima "--nama nilai" maupun "--nama=nilai".</summary>
    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..];

            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }
}
