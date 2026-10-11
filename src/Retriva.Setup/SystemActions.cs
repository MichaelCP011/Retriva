using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Retriva.Setup;

/// <summary>
/// Perintah sistem yang dijalankan installer: layanan, firewall, registry.
///
/// SETIAP TINDAKAN DI SINI PUNYA PEMBATALANNYA, dan itu syarat masuk berkas ini.
///
/// Installer buatan sendiri paling sering gagal bukan saat memasang, melainkan
/// saat gagal memasang. Layanan sudah terdaftar sementara berkasnya belum
/// lengkap, aturan firewall sudah ada untuk program yang tidak jadi dipasang,
/// entri Add/Remove Programs menunjuk ke folder kosong — mesin tertinggal dalam
/// keadaan yang tidak dapat diperbaiki dengan memasang ulang maupun dengan
/// uninstall.
///
/// Karena itu tidak ada tindakan yang boleh berdiri sendiri di sini. Yang tidak
/// dapat dibatalkan tidak boleh dilakukan installer.
/// </summary>
public static class SystemActions
{
    /// <summary>
    /// Menjalankan program konsol dan menunggunya selesai.
    ///
    /// Keluarannya dikembalikan, bukan dibuang. Ketika sc atau netsh menolak,
    /// alasannya ada di stdout — dan installer yang hanya melaporkan "gagal"
    /// memaksa orangnya menebak apa yang ditolak.
    /// </summary>
    public static (int Code, string Output) Run(string file, string arguments)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"tidak dapat menjalankan {file}");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        // Batas waktu supaya installer tidak menggantung selamanya bila sc
        // menunggu layanan yang tidak pernah menjawab.
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* sudah mati */ }
            return (-1, output + "\n(perintah melewati batas waktu 60 detik)");
        }

        return (process.ExitCode, output.Trim());
    }

    /* ================= layanan ================= */

    public static bool ServiceExists(string name) =>
        Run("sc", $"query \"{name}\"").Code == 0;

    public static void CreateService(string name, string display, string exePath, string description)
    {
        // Spasi setelah "binPath=" WAJIB. sc.exe memakai bentuk "kunci= nilai",
        // dan tanpa spasinya perintah ditolak dengan pesan tentang sintaks yang
        // tidak menyebut spasi sama sekali.
        var (code, output) = Run("sc",
            $"create \"{name}\" binPath= \"\\\"{exePath}\\\"\" start= auto DisplayName= \"{display}\"");

        if (code != 0) throw new InvalidOperationException($"sc create gagal: {output}");

        Run("sc", $"description \"{name}\" \"{description}\"");

        /*
            Pemulihan otomatis.

            Layanan yang mati dan tidak dihidupkan ulang membuat unit menghilang
            dari dashboard tanpa sebab yang terlihat — dan tidak ada yang membuka
            Service Manager sampai praktikum sudah berjalan.
        */
        Run("sc", $"failure \"{name}\" reset= 86400 actions= restart/5000/restart/10000/restart/30000");
    }

    public static void StartService(string name)
    {
        var (code, output) = Run("sc", $"start \"{name}\"");

        // 1056 = layanan sudah berjalan. Bukan kegagalan.
        if (code is not (0 or 1056))
            throw new InvalidOperationException($"sc start gagal: {output}");
    }

    /// <summary>
    /// Menghentikan layanan dan MENUNGGU sampai benar-benar berhenti.
    ///
    /// sc stop hanya mengirim permintaan; ia kembali sebelum layanannya mati.
    /// Mengganti berkas exe pada detik berikutnya akan gagal karena berkasnya
    /// masih terkunci — dan pesan galatnya berbunyi "access denied", yang
    /// menuntun ke arah hak akses alih-alih ke proses yang belum berhenti.
    /// </summary>
    public static void StopService(string name, TimeSpan patience)
    {
        if (!ServiceExists(name)) return;

        Run("sc", $"stop \"{name}\"");

        var deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline)
        {
            var (_, output) = Run("sc", $"query \"{name}\"");
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(400);
        }

        throw new InvalidOperationException(
            $"layanan {name} tidak berhenti dalam {patience.TotalSeconds:N0} detik");
    }

    public static void DeleteService(string name)
    {
        if (!ServiceExists(name)) return;
        Run("sc", $"delete \"{name}\"");
    }

    /* ================= firewall ================= */

    /// <summary>
    /// Menambahkan aturan firewall lewat netsh.
    ///
    /// Nama aturannya dipakai juga untuk menghapusnya, jadi ia harus tetap dan
    /// unik. Aturan lama dengan nama yang sama dihapus lebih dulu: netsh
    /// MENAMBAH aturan kedua alih-alih menimpanya, dan pemasangan berulang akan
    /// meninggalkan tumpukan aturan kembar yang tidak pernah dibersihkan.
    /// </summary>
    public static void AddFirewallRule(string name, string arguments)
    {
        RemoveFirewallRule(name);

        var (code, output) = Run("netsh",
            $"advfirewall firewall add rule name=\"{name}\" {arguments}");

        if (code != 0) throw new InvalidOperationException($"netsh gagal: {output}");
    }

    public static void RemoveFirewallRule(string name) =>
        Run("netsh", $"advfirewall firewall delete rule name=\"{name}\"");

    /* ================= Add/Remove Programs ================= */

    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Mendaftarkan aplikasi di Add/Remove Programs.
    ///
    /// TANPA INI, ORANG BERIKUTNYA TIDAK PUNYA CARA MENCOPOTNYA.
    ///
    /// Aplikasi yang tidak muncul di Apps &amp; features hanya dapat dihapus oleh
    /// yang tahu di mana berkasnya, tahu nama layanannya, dan tahu aturan
    /// firewall mana yang harus dicabut. Di lab yang berganti asisten tiap
    /// tahun, itu berarti tidak ada yang bisa menghapusnya sama sekali.
    ///
    /// EstimatedSize ikut ditulis karena Windows menampilkannya di daftar, dan
    /// baris tanpa ukuran terlihat seperti pemasangan yang rusak.
    /// </summary>
    public static void RegisterUninstall(
        string key, string displayName, string version, string publisher,
        string installDir, string uninstallExe, long sizeBytes)
    {
        using var root = Registry.LocalMachine.CreateSubKey($@"{UninstallRoot}\{key}", true)
            ?? throw new InvalidOperationException("tidak dapat menulis entri Add/Remove Programs");

        root.SetValue("DisplayName", displayName);
        root.SetValue("DisplayVersion", version);
        root.SetValue("Publisher", publisher);
        root.SetValue("InstallLocation", installDir);
        root.SetValue("DisplayIcon", uninstallExe);
        root.SetValue("UninstallString", $"\"{uninstallExe}\" --uninstall");
        root.SetValue("QuietUninstallString", $"\"{uninstallExe}\" --uninstall --silent");
        root.SetValue("EstimatedSize", (int)(sizeBytes / 1024), RegistryValueKind.DWord);
        root.SetValue("NoModify", 1, RegistryValueKind.DWord);
        root.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        root.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    public static void UnregisterUninstall(string key)
    {
        try { Registry.LocalMachine.DeleteSubKeyTree($@"{UninstallRoot}\{key}", false); }
        catch (Exception) { /* sudah tidak ada */ }
    }

    /* ================= autostart klien ================= */

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Menjalankan klien saat ada yang login.
    ///
    /// HKLM, bukan HKCU. Run key milik mesin berlaku untuk pengguna mana pun
    /// yang login; milik pengguna hanya berlaku untuk akun yang sedang dipakai
    /// saat pemasangan. Di lab yang memakai autologon dengan akun berbeda-beda,
    /// HKCU berarti klien tidak pernah jalan di sebagian PC — dan unit itu
    /// tampil online tetapi tidak dapat dikunci maupun dipantau.
    /// </summary>
    public static void SetClientAutostart(string valueName, string command)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("tidak dapat menulis Run key");
        key.SetValue(valueName, command);
    }

    public static void ClearClientAutostart(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RunKey, true);
            key?.DeleteValue(valueName, false);
        }
        catch (Exception) { /* sudah tidak ada */ }
    }

    /* ================= hak akses berkas ================= */

    /// <summary>
    /// Memberi pengguna biasa hak mengubah sebuah berkas.
    ///
    /// INSTALLER BERJALAN SEBAGAI ADMINISTRATOR; APLIKASINYA TIDAK.
    ///
    /// Berkas yang dibuat proses terelevasi di ProgramData dimiliki grup
    /// Administrators. Pengguna biasa dapat membacanya dan tidak dapat
    /// mengubahnya — dan panel admin berjalan sebagai pengguna biasa, tanpa UAC,
    /// karena dosen membukanya setiap kelas.
    ///
    /// Akibatnya panel dapat dibuka, menyambung, dan disetujui asisten — lalu
    /// gagal menyimpan lab yang baru dikenalinya. Pada pembukaan berikutnya ia
    /// kembali "belum tertaut", dan seluruh pendaftarannya harus diulang. Tidak
    /// ada galat yang menyebut hak akses; yang terlihat hanya persetujuan yang
    /// tidak pernah bertahan.
    ///
    /// icacls dipakai alih-alih API ACL terkelola karena hasilnya berupa teks
    /// yang dapat dibaca di log pemasangan. ACL yang gagal diatur lewat API
    /// melempar pengecualian yang menyebut jenis SID, bukan berkas mana yang
    /// bermasalah.
    /// </summary>
    public static void GrantUsersModify(string path)
    {
        // *S-1-5-32-545 adalah SID grup Users, dipakai alih-alih nama "Users"
        // supaya perintahnya tetap benar di Windows berbahasa lain — di sana
        // grupnya bernama "Usuarios", "Utilisateurs", dan seterusnya.
        var (code, output) = Run("icacls", $"\"{path}\" /grant *S-1-5-32-545:(M) /C");

        if (code != 0) throw new InvalidOperationException($"icacls gagal: {output}");
    }

    /* ================= berkas ================= */

    /// <summary>Ukuran seluruh isi folder, untuk EstimatedSize.</summary>
    public static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
