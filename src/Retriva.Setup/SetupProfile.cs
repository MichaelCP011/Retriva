using System.IO;
using System.Text.Json;

namespace Retriva.Setup;

/// <summary>
/// Peran mesin, bukan komponen aplikasi.
///
/// PEMBAGIANNYA MENGIKUTI MESIN, BUKAN PROYEK.
///
/// Godaan yang wajar adalah membuat satu installer per proyek: Agent, Client,
/// Admin. Tetapi Agent dan Client SELALU dipasang bersama — Client adalah tangan
/// Agent di sesi pengguna, karena layanan di Session 0 tidak dapat menampilkan
/// jendela, membaca layar, maupun menyuntikkan input.
///
/// Memisahkannya menjadi dua installer menciptakan keadaan "Agent ada, Client
/// tidak". Unit itu tampil online di dashboard dan menjalankan setiap perintah
/// dengan benar; yang diam hanya kunci layar, penonton siaran, dan pemantauan
/// ujian. Ketiganya gagal tanpa satu galat pun, dan lini masa ujiannya kosong —
/// yang terbaca sebagai bersih.
///
/// Keadaan itu tidak dibuat mustahil oleh disiplin, melainkan oleh tidak adanya
/// installer yang mampu menghasilkannya.
/// </summary>
public enum SetupRole
{
    /// <summary>PC mahasiswa: Agent (layanan) + Client (sesi pengguna).</summary>
    Lab,

    /// <summary>PC Asisten atau laptop dosen: panel admin, yang juga dapat menjadi koordinator.</summary>
    Panel,
}

/// <summary>Pilihan yang dikumpulkan wizard, lalu dipakai menyusun rencana.</summary>
public sealed class SetupProfile
{
    public SetupRole Role { get; set; } = SetupRole.Lab;

    /// <summary>
    /// Alamat koordinator yang dituju agent. Hanya untuk peran Lab.
    ///
    /// Bawaannya SENGAJA loopback, bukan alamat lab yang ditebak.
    ///
    /// Versi pertama memakai 192.168.1.252 — alamat PC Asisten menurut rencana
    /// awal. Nilai itu benar di satu lab dan salah di setiap tempat lain,
    /// termasuk di mesin pengembang yang menguji lokal, dan orang yang
    /// melihatnya cenderung menerimanya begitu saja karena ia terlihat seperti
    /// hasil deteksi.
    ///
    /// 127.0.0.1 tidak dapat disalahartikan sebagai tebakan yang pintar. Ia
    /// benar untuk uji satu mesin, dan jelas-jelas perlu diganti untuk yang
    /// lain — sementara tombol pencarian di sebelahnya mengisinya sendiri bila
    /// koordinatornya memang ada di jaringan.
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8443;

    /// <summary>Kode pendaftaran agent dan sidik jari sertifikat koordinator.</summary>
    public string EnrollmentSecret { get; set; } = "";
    public string Thumbprint { get; set; } = "";

    /// <summary>
    /// Mesin ini menjalankan koordinator. Hanya untuk peran Panel.
    ///
    /// Satu pertanyaan, bukan beberapa saklar — persis seperti PanelConfig
    /// menyatakannya. Semua yang lain diturunkan darinya.
    /// </summary>
    public bool HostsLab { get; set; }

    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Folder program. Kosong berarti pakai bawaan di Program Files.
    ///
    /// DAPAT DIUBAH, TETAPI BAWAANNYA TETAP YANG BENAR untuk hampir semua orang.
    ///
    /// Program Files diberi hak yang tepat oleh Windows sendiri: pengguna biasa
    /// dapat menjalankan isinya tetapi tidak dapat mengubahnya, yang persis
    /// dibutuhkan aplikasi yang mengunci layar dan memantau ujian. Memasangnya
    /// di folder yang dapat ditulis mahasiswa berarti exe-nya dapat diganti
    /// mahasiswa.
    ///
    /// Yang membuatnya perlu dapat diubah adalah lab dengan drive sistem kecil,
    /// dan mesin dengan kebijakan yang melarang pemasangan di partisi C.
    /// </summary>
    public string? InstallDirOverride { get; set; }

    public string DefaultInstallDir => Role == SetupRole.Lab
        ? Path.Combine(ProgramFiles, "Retriva", "Lab")
        : Path.Combine(ProgramFiles, "Retriva", "Panel");

    public string InstallDir => string.IsNullOrWhiteSpace(InstallDirOverride)
        ? DefaultInstallDir
        : Path.GetFullPath(InstallDirOverride.Trim());

    /// <summary>
    /// Folder tujuan berkas yang dibagikan asisten. Hanya untuk peran Lab.
    ///
    /// Kosong berarti Documents\Retriva milik mahasiswa yang sedang login —
    /// tempat yang ia tahu cara membukanya tanpa diberi tahu. Diubah ketika
    /// berkas praktikum berukuran besar dan drive sistemnya kecil.
    ///
    /// Disimpan ke agent.json, bukan dipakai langsung oleh installer: yang
    /// mengunduh adalah agent, dan folder ini baru ada gunanya saat itu terjadi.
    /// </summary>
    public string? FileInboxDir { get; set; }

    public static string DefaultFileInbox => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Retriva");

    public string UninstallKey => Role == SetupRole.Lab ? "RetrivaLab" : "RetrivaPanel";

    public string ProductName => Role == SetupRole.Lab
        ? "Retriva Lab (PC Mahasiswa)"
        : "Retriva Panel Admin";

    public static string ProgramFiles =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Retriva");

    public const string ServiceName = "RetrivaAgent";
    public const string Version = "1.0.0";
    public const string Publisher = "Laboratorium Information Retrieval";

    /// <summary>
    /// Menuliskan agent.json TANPA menghapus token perangkat yang sudah ada.
    ///
    /// Token adalah satu-satunya bukti bahwa unit ini pernah disetujui. Menimpa
    /// berkasnya pada pemasangan ulang akan membuat unit mendaftar sebagai unit
    /// baru — kehilangan namanya beserta seluruh riwayat percakapan, berkas, dan
    /// catatan ujiannya. Tanpa satu galat pun; yang terlihat hanya unit bernama
    /// aneh yang riwayatnya mendadak kosong.
    /// </summary>
    public void WriteAgentConfig(Action<string>? log = null)
    {
        Directory.CreateDirectory(DataDir);
        var file = Path.Combine(DataDir, "agent.json");

        string? token = null;
        if (File.Exists(file))
        {
            try
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(file));
                if (existing.RootElement.TryGetProperty("deviceToken", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    token = value.GetString();
                }
            }
            catch (JsonException ex)
            {
                // agent.json rusak diperlakukan sebagai tanpa token, dan itu
                // dicatat. Diam-diam menimpanya berarti kehilangan token yang
                // mungkin masih bisa diselamatkan dengan tangan.
                log?.Invoke($"agent.json lama tidak terbaca ({ex.Message}) — token tidak dipertahankan");
            }

            log?.Invoke(string.IsNullOrEmpty(token)
                ? "Tanpa token — unit akan mendaftar sebagai unit baru."
                : "Token perangkat ditemukan dan dipertahankan.");
        }

        var config = new Dictionary<string, object?>
        {
            ["consoleEndpoints"] = new[] { Host },
            ["consolePort"] = Port,
            ["enrollmentSecret"] = EnrollmentSecret,
            ["consoleCertificateThumbprint"] = Thumbprint,
            ["powerControlMode"] = "real",
        };

        // Hanya ditulis bila memang dipilih. Menulis nilai bawaannya secara
        // eksplisit akan membekukan folder itu ke dalam berkas, sehingga
        // perubahan bawaan di versi berikutnya tidak berlaku untuk mesin yang
        // sebenarnya tidak pernah memilih apa pun.
        if (!string.IsNullOrWhiteSpace(FileInboxDir))
            config["fileInboxDirectory"] = FileInboxDir.Trim();

        if (!string.IsNullOrEmpty(token)) config["deviceToken"] = token;

        File.WriteAllText(file, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true }));

        log?.Invoke($"agent.json ditulis ke {file}");
    }

    /// <summary>
    /// Menuliskan panel.json bila belum ada.
    ///
    /// TIDAK MENIMPA yang sudah ada, karena di dalamnya tersimpan daftar lab
    /// yang pernah menyetujui panel ini beserta token perangkatnya. Menimpanya
    /// memaksa dosen mendaftar ulang ke setiap lab tempat ia mengajar, dan
    /// menunggu asisten menyetujuinya lagi satu per satu.
    /// </summary>
    public void WritePanelConfig(Action<string>? log = null)
    {
        Directory.CreateDirectory(DataDir);
        var file = Path.Combine(DataDir, "panel.json");

        if (File.Exists(file))
        {
            log?.Invoke("panel.json sudah ada — dipertahankan beserta lab yang dikenali.");
            return;
        }

        var config = new Dictionary<string, object?>
        {
            ["isSuperadmin"] = HostsLab,
            ["configured"] = true,
            ["deviceId"] = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            ["displayName"] = DisplayName,
            ["labs"] = Array.Empty<object>(),
        };

        File.WriteAllText(file, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true }));

        log?.Invoke($"panel.json ditulis ke {file}");
    }
}
