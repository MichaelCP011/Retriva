using System.Text.Json.Serialization;

namespace Retriva.Shared.Config;

/// <summary>
/// Isi agent.json di PC mahasiswa. Sengaja minimal — agent tidak perlu tahu
/// konfigurasi jaringannya sendiri, hanya ke mana ia harus menyambung.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Dicoba berurutan: primary dulu, lalu secondary.</summary>
    public List<string> ConsoleEndpoints { get; set; } = new()
    {
        "192.168.1.252",
        "192.168.1.253",
    };

    public int ConsolePort { get; set; } = 8443;
    public string EnrollmentSecret { get; set; } = string.Empty;
    /// <summary>Sidik jari SHA-256 sertifikat konsol; menolak admin palsu.</summary>
    public string ConsoleCertificateThumbprint { get; set; } = string.Empty;
    /// <summary>Diisi setelah pendaftaran pertama berhasil.</summary>
    public string? DeviceToken { get; set; }
    /// <summary>
    /// Memaksa nama unit, mengalahkan nama yang diberikan koordinator.
    ///
    /// Kosongkan pada pemasangan sungguhan: koordinatorlah yang menamai unit,
    /// memakai rentang alamat di console.json miliknya. Medan ini alat
    /// pengembangan, supaya beberapa agent tiruan di satu mesin dapat dibedakan.
    ///
    /// Komentar lamanya berbunyi "kosongkan agar nomor unit diturunkan dari
    /// alamat IP" — dan penurunan itu memang ada di agent, memakai konfigurasi
    /// bawaan yang tidak pernah cocok dengan lab mana pun.
    /// </summary>
    public string? UnitIdOverride { get; set; }

    public int ReconnectSeconds { get; set; } = 5;

    /// <summary>
    /// "real" menjalankan perintah daya sungguhan; "simulate" hanya mencatat.
    /// Selalu setel "simulate" di mesin pengembangan — atau pakai variabel
    /// lingkungan RETRIVA_POWER=simulate — supaya tidak mematikan laptop sendiri
    /// saat menguji tombol di dashboard.
    /// </summary>
    public string PowerControlMode { get; set; } = "real";

    /// <summary>
    /// Folder tujuan berkas yang dibagikan asisten. Kosong berarti Documents\Retriva.
    ///
    /// DAPAT DIPILIH KARENA UKURANNYA TIDAK DAPAT DIPERKIRAKAN.
    ///
    /// Folder bawaan berada di profil pengguna, yang di banyak lab duduk di
    /// partisi sistem berukuran kecil. Satu berkas praktikum tujuh gigabyte
    /// dikalikan empat puluh PC adalah keadaan yang wajar terjadi, dan
    /// akibatnya bukan galat yang menyebut "disk penuh" — melainkan Windows
    /// yang mulai gagal pada hal-hal yang tidak berhubungan sama sekali.
    ///
    /// Dibaca lewat FileDownloader.InboxDir, bukan langsung, supaya agent dan
    /// klien tidak pernah dapat menunjuk ke dua folder yang berbeda.
    /// </summary>
    public string? FileInboxDirectory { get; set; }
}

/// <summary>
/// Isi panel.json: identitas panel admin dan ke mana ia menyambung.
///
/// Terpisah dari ConsoleConfig karena keduanya menjawab pertanyaan berbeda.
/// ConsoleConfig menggambarkan sebuah lab — subnet, jumlah unit, port. Berkas
/// ini menggambarkan satu panel: siapa dia dan koordinator mana yang ia
/// percayai. Laptop dosen punya yang kedua dan tidak punya yang pertama.
/// </summary>
public sealed class PanelConfig
{
    /// <summary>
    /// True bila mesin ini yang menjalankan koordinator.
    ///
    /// SATU-SATUNYA PILIHAN DI PEMASANG ADMIN. Semua yang lain diturunkan
    /// darinya: mesin Superadmin membuka port dan menyimpan basis data, mesin
    /// lain tidak membuka port apa pun dan hanya menyambung. Menjadikannya
    /// beberapa saklar terpisah membuat kombinasi yang tidak masuk akal dapat
    /// tersetel — misalnya panel yang membuka port tetapi tidak punya data.
    /// </summary>
    public bool IsSuperadmin { get; set; }

    /// <summary>
    /// Pertanyaan penyiapan sudah dijawab.
    ///
    /// Perlu terpisah dari IsSuperadmin karena "false" adalah jawaban yang sah
    /// — panel yang bergabung ke lab lain. Tanpa penanda ini, satu-satunya cara
    /// mengetahui apakah orangnya sudah menjawab adalah melihat IsSuperadmin
    /// bernilai false, yang juga nilai bawaannya. Akibatnya dialog muncul lagi
    /// setiap kali aplikasi dibuka.
    /// </summary>
    public bool Configured { get; set; }

    /// <summary>Identitas panel ini, dibuat sekali lalu tetap.</summary>
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// SELURUH lab yang pernah menyetujui panel ini, bukan hanya yang terakhir.
    ///
    /// INI YANG MEMBUAT DOSEN DAPAT BERPINDAH RUANGAN.
    ///
    /// Versi sebelumnya menyimpan satu koordinator saja. Dosen yang mengajar di
    /// lab lain harus melepas tautannya — dan ketika kembali ke lab pertama,
    /// mendaftar lagi dari nol, menunggu asisten menyetujuinya lagi. Padahal
    /// dosen memang mengajar di lebih dari satu ruangan, dan aplikasi yang
    /// menuntut pendaftaran ulang setiap perpindahan akan ditinggalkan sebelum
    /// semester berakhir.
    ///
    /// Setiap lab dikenali dari sidik jari sertifikatnya, bukan alamat IP-nya.
    /// Alamat berubah setiap DHCP memberi nomor lain; sertifikat tidak berubah
    /// selama koordinatornya tidak diinstal ulang.
    /// </summary>
    public List<KnownLab> Labs { get; set; } = [];

    /// <summary>Sudah pernah disetujui setidaknya satu lab.</summary>
    [JsonIgnore]
    public bool IsEnrolled => Labs.Count > 0;

    /// <summary>Lab yang paling terakhir dipakai, untuk dicoba lebih dulu.</summary>
    [JsonIgnore]
    public KnownLab? MostRecentLab =>
        Labs.OrderByDescending(l => l.LastUsed).FirstOrDefault();

    public KnownLab? FindByThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? null
            : Labs.FirstOrDefault(l =>
                  string.Equals(l.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>Mencatat atau memperbarui sebuah lab yang dikenali.</summary>
    public KnownLab Remember(string thumbprint, string labName, string host, int port, string? token)
    {
        var lab = FindByThumbprint(thumbprint);

        if (lab is null)
        {
            lab = new KnownLab { Thumbprint = thumbprint };
            Labs.Add(lab);
        }

        lab.LabName = labName;

        // Alamat diperbarui setiap kali, sidik jari tidak. Koordinator yang
        // sama dapat berpindah alamat; kalau alamatnya yang dijadikan identitas,
        // satu perubahan DHCP membuatnya tampak seperti lab yang belum dikenal.
        lab.Host = host;
        lab.Port = port;
        lab.LastUsed = DateTimeOffset.UtcNow;

        // Token hanya ditimpa bila ada yang baru. Penyambungan ulang tidak
        // menerbitkan token, dan menimpanya dengan null akan menghapus satu-
        // satunya bukti bahwa panel ini pernah disetujui di sini.
        if (!string.IsNullOrWhiteSpace(token)) lab.DeviceToken = token;

        return lab;
    }

    public void Forget(string thumbprint) =>
        Labs.RemoveAll(l => string.Equals(l.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Sebuah lab yang pernah menyetujui panel ini.</summary>
public sealed class KnownLab
{
    /// <summary>Identitas lab. Stabil selama koordinatornya tidak diinstal ulang.</summary>
    public string Thumbprint { get; set; } = "";

    public string LabName { get; set; } = "";

    /// <summary>Alamat terakhir yang diketahui. Petunjuk, bukan identitas.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8443;

    /// <summary>Bukti bahwa panel ini pernah disetujui di lab tersebut.</summary>
    public string DeviceToken { get; set; } = "";

    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Isi console.json di PC admin.</summary>
public sealed class ConsoleConfig
{
    public string Role { get; set; } = "Primary";

    /// <summary>
    /// Nama lab yang ditampilkan di panel.
    ///
    /// Bukan hiasan: begitu ada lebih dari satu lab di gedung yang sama, panel
    /// dosen dapat menyambung ke koordinator yang salah tanpa ada satu pun
    /// tanda bahwa itu terjadi — sampai perintah kunci layar mendarat di kelas
    /// orang lain. Nama lab di kepala jendela membuatnya kentara sebelum itu.
    /// </summary>
    public string LabName { get; set; } = "Laboratorium Information Retrieval";

    /// <summary>
    /// Awalan alamat lab. Dipakai dua hal yang nyata: alamat siar Wake-on-LAN,
    /// dan penyaring agar hanya mesin di subnet ini yang diberi nomor unit.
    /// </summary>
    public string SubnetPrefix { get; set; } = "192.168.1.";

    /// <summary>
    /// Oktet terakhir → nama unit. Daftar, bukan rumus.
    ///
    /// RUMUS BERURUTAN DIHAPUS KARENA LAB TIDAK BERURUTAN.
    ///
    /// Versi sebelumnya menyimpan oktet awal dan jumlah PC, lalu menamai unit
    /// dengan menghitung: PC-01 di oktet awal, PC-02 satu setelahnya, dan
    /// seterusnya. Model itu mensyaratkan barisan yang tak terputus.
    ///
    /// Lab sungguhan memutusnya. Alamat .1 dihindari karena lazim dipakai
    /// gerbang jaringan, jadi PC-01 pindah ke .43 sementara PC-02 tetap di .2 —
    /// dan tidak ada oktet awal mana pun yang dapat menghasilkan kedua nomor
    /// itu sekaligus. Rumusnya bukan kurang lengkap, melainkan salah bentuk.
    ///
    /// Kuncinya oktet, bukan alamat penuh: seluruh unit berada di subnet yang
    /// sama, jadi menyimpan bagian yang sama empat puluh kali hanya menambah
    /// tempat nilai itu bisa tidak sinkron.
    ///
    /// Berlaku hanya pada pendaftaran PERTAMA. Unit yang sudah memegang token
    /// perangkat mempertahankan namanya walau alamatnya berubah — lihat
    /// UnitStore.Validate. Tanpa itu, satu sewa DHCP yang berbeda akan menukar
    /// nama dua PC tanpa satu pun tanda bahwa itu terjadi.
    /// </summary>
    public Dictionary<int, string> UnitNames { get; set; } = [];

    /// <summary>
    /// Nilai rumus lama, dibaca sekali lalu diubah menjadi daftar.
    ///
    /// Ada semata-mata agar console.json yang sudah terlanjur ada tidak
    /// kehilangan penomorannya. Tanpa ini, lab yang sudah berjalan mendapat
    /// tabel kosong dan setiap unit yang mendaftar ulang akan bernama seperti
    /// nama mesin Windows-nya — kegagalan yang muncul sebagai grid yang
    /// mendadak tidak dikenali, tanpa hubungan yang terlihat dengan
    /// penggantian sebuah model konfigurasi.
    /// </summary>
    [JsonPropertyName("studentStartOctet")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyStartOctet { get; set; }

    [JsonPropertyName("studentCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyStudentCount { get; set; }

    /// <summary>Mengubah rumus lama menjadi daftar. Aman dipanggil berulang.</summary>
    public bool AdoptLegacyRange()
    {
        if (UnitNames.Count > 0) { LegacyStartOctet = null; LegacyStudentCount = null; return false; }
        if (LegacyStartOctet is not { } start || LegacyStudentCount is not { } count) return false;

        for (var n = 1; n <= count; n++)
        {
            var octet = start + n - 1;
            if (octet is > 0 and < 255) UnitNames[octet] = $"PC-{n:D2}";
        }

        LegacyStartOctet = null;
        LegacyStudentCount = null;
        return true;
    }

    public int ListenPort { get; set; } = 8443;
    public string DataDirectory { get; set; } = @"C:\ProgramData\Retriva";

    /// <summary>
    /// Kode pendaftaran untuk AGENT, bukan untuk panel admin.
    ///
    /// PENAMAANNYA SENGAJA PANJANG. Ada dua rahasia di sistem ini dan keduanya
    /// sempat sama-sama disebut "kode pendaftaran":
    ///
    ///   yang ini  — hex panjang, berumur selamanya, ditanam installer ke 40 PC
    ///   kode panel — 12 karakter, berumur 10 menit, dibacakan sekali ke dosen
    ///
    /// Keduanya menuntut penanganan yang berlawanan. Menyalin yang ini ke
    /// tempat yang menuntut kode panel akan gagal dengan sopan, tetapi
    /// menyalinnya ke arah sebaliknya — menempelkan kode selamanya di tempat
    /// yang seharusnya berumur menit — gagal tanpa satu pun tanda.
    /// </summary>
    public string AgentEnrollmentSecret { get; set; } = string.Empty;

    /// <summary>
    /// Nama lama dari AgentEnrollmentSecret, dibaca sekali lalu dipindahkan.
    ///
    /// Ada semata-mata agar console.json yang sudah terlanjur ada tidak
    /// kehilangan kodenya saat berganti nama. Tanpa ini kode baru diterbitkan
    /// dan seluruh 40 agent yang sudah terdaftar akan ditolak — kegagalan yang
    /// muncul sebagai lab yang mendadak sepi, tanpa hubungan yang terlihat
    /// dengan penggantian nama sebuah properti.
    /// </summary>
    /// Tidak ditulis kembali setelah kosong, supaya console.json tidak
    /// menyimpan kunci mati yang tampak seperti setelan yang bisa diisi.
    [JsonPropertyName("enrollmentSecret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEnrollmentSecret { get; set; }

    /// <summary>Memindahkan nilai lama bila ada. Aman dipanggil berulang.</summary>
    public bool AdoptLegacySecret()
    {
        if (string.IsNullOrWhiteSpace(LegacyEnrollmentSecret)) return false;
        if (!string.IsNullOrWhiteSpace(AgentEnrollmentSecret)) return false;

        AgentEnrollmentSecret = LegacyEnrollmentSecret;
        LegacyEnrollmentSecret = null;
        return true;
    }

    public bool RequireStudentLogin { get; set; } = true;
    public bool AllowGuestAccess { get; set; } = true;
    public bool StudentsMayRejectControl { get; set; }

    /// <summary>Nama unit untuk sebuah oktet, null bila belum dipetakan.</summary>
    public string? UnitNameForOctet(int octet) =>
        UnitNames.TryGetValue(octet, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;
}
