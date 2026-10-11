namespace Retriva.Shared;

/// <summary>
/// Lokasi berkas runtime. Semua komponen memakai jalur yang sama supaya
/// agent (berjalan sebagai SYSTEM) dan konsol menemukan berkas yang benar.
/// </summary>
public static class RetrivaPaths
{
    /// <summary>
    /// Bisa ditimpa lewat variabel RETRIVA_DATA_DIR — berguna saat menjalankan
    /// beberapa instance sekaligus di satu mesin untuk pengujian.
    /// </summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("RETRIVA_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Retriva");

    public static string AgentConfigFile   => Path.Combine(DataDir, "agent.json");
    public static string ConsoleConfigFile => Path.Combine(DataDir, "console.json");
    public static string CertificateFile   => Path.Combine(DataDir, "console.pfx");
    public static string ThumbprintFile    => Path.Combine(DataDir, "console.thumbprint");
    public static string DeviceRegistryFile=> Path.Combine(DataDir, "devices.json");
    /// <summary>Identitas panel admin: id perangkat, nama tampilan, token.</summary>
    public static string PanelIdentityFile => Path.Combine(DataDir, "panel.json");

    /// <summary>
    /// Folder log, SATU untuk seluruh komponen di mesin ini.
    ///
    /// SEBELUMNYA LOG TERSEBAR DI TIGA TEMPAT YANG BERBEDA PER AKUN.
    ///
    /// Ketiga komponen menulis ke Path.GetTempPath(), yang terdengar seragam
    /// tetapi tidak: nilainya berbeda untuk tiap akun Windows. Panel berjalan
    /// sebagai dosen atau asisten, jadi lognya mendarat di
    /// C:\Users\&lt;nama&gt;\AppData\Local\Temp. Agent berjalan sebagai LocalSystem,
    /// jadi lognya mendarat di C:\Windows\Temp — folder yang bahkan tidak
    /// terlihat dari Explorer tanpa mengetik jalurnya.
    ///
    /// Akibatnya sederhana dan mahal: orang yang diminta mengirim log tidak
    /// dapat menemukannya, dan tidak ada satu pun tempat di aplikasi yang
    /// menyebutkan letaknya.
    ///
    /// ProgramData\Retriva\logs dipilih karena ia sama untuk setiap akun,
    /// berdampingan dengan basis data dan sertifikat, dan sudah dibuat oleh
    /// installer dengan hak yang benar.
    /// </summary>
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Jalur berkas log untuk sebuah komponen, foldernya dibuat bila perlu.</summary>
    public static string LogFile(string component)
    {
        var dir = LogDir;

        // Kegagalan membuat folder TIDAK boleh menjatuhkan komponennya. Log
        // adalah alat bantu; aplikasi yang menolak berjalan karena tidak dapat
        // menulis log adalah pertukaran yang jelas salah di ruang praktikum.
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dir = Path.GetTempPath();
        }

        return Path.Combine(dir, $"retriva-{component}.log");
    }

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
