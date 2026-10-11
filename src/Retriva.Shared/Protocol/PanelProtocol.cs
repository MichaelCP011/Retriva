using System.Text.Json.Serialization;

namespace Retriva.Shared.Protocol;

/// <summary>
/// Protokol antara panel admin dan Superadmin.
///
/// Terpisah dari protokol agent karena persoalannya berbeda arah: agent adalah
/// mesin yang DIKENDALIKAN, panel adalah orang yang MENGENDALIKAN. Menyatukan
/// keduanya dalam satu hub berarti setiap wewenang harus diperiksa ulang di
/// setiap metode, dan satu yang terlewat membuka seluruh lab.
///
/// GAGASAN UTAMANYA: panel tidak pernah tahu connection id, alamat IP, atau
/// grup multicast. Ia menyebut unit dengan namanya dan menyerahkan sisanya.
/// Dengan begitu panel di laptop dosen dan panel di PC Asisten memakai jalur
/// yang sama persis — dan tidak ada cabang yang hanya teruji di satu sisi.
/// </summary>
public static class PanelProtocol
{
    public const string HubPath = "/panel";

    /// <summary>
    /// Panel menyambung lewat SignalR bahkan ketika berada di mesin yang sama
    /// dengan Superadmin. Terasa berputar, tetapi justru itu maksudnya: satu
    /// jalur kode untuk semua panel. Jalur pintas dalam-proses akan menjadi
    /// jalur yang paling sering dipakai saat mengembangkan sekaligus paling
    /// jarang teruji di lab.
    /// </summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>
    /// Grup dan port beacon penemuan lab.
    ///
    /// Grup yang sama dengan siaran layar, port berbeda. Satu grup multicast
    /// lebih mudah dijelaskan ke pengelola jaringan daripada tiga.
    /// </summary>
    public const string DiscoveryGroup = "239.77.82.84";
    public const int DiscoveryPort = 9102;

    /// <summary>
    /// Port tempat Superadmin menerima aliran siaran unicast dari panel.
    ///
    /// Panel TIDAK PERNAH memancarkan multicast sendiri. Ia mengirim satu
    /// aliran ke sini, dan Superadmin — yang duduk di kabel gigabit — yang
    /// memancarkannya ke empat puluh unit.
    /// </summary>
    public const int RelayPort = 9103;

    /// <summary>
    /// Jarak antar beacon.
    ///
    /// Dua detik: panel dosen yang baru dibuka menemukan labnya sebelum orang
    /// sempat bertanya-tanya apakah aplikasinya bekerja. Cukup jarang sehingga
    /// tidak berarti apa-apa bagi jaringan — satu datagram kecil, bukan aliran.
    /// </summary>
    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Berapa lama sebuah lab tetap ditampilkan setelah beacon terakhirnya.
    ///
    /// Tiga kali jarak beacon, supaya satu datagram yang hilang — hal biasa di
    /// Wi-Fi — tidak membuat kartu labnya berkedip hilang lalu muncul lagi.
    /// </summary>
    public static readonly TimeSpan BeaconTimeout = TimeSpan.FromSeconds(6);
}

/// <summary>
/// Isi beacon yang dipancarkan Superadmin.
///
/// SENGAJA TIDAK MEMBAWA RAHASIA APA PUN. Isinya hanya cukup untuk menyambung,
/// dan menyambung saja tidak memberi wewenang apa-apa — panel yang datang tetap
/// menunggu disetujui. Beacon yang membawa rahasia akan menyiarkannya ke
/// seluruh subnet dua kali per detik.
/// </summary>
public sealed record LabBeacon
{
    public required string LabName { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>Menerima permintaan bergabung saat ini.</summary>
    public bool AcceptingPanels { get; init; }

    /// <summary>
    /// Sidik jari sertifikat, untuk DITAMPILKAN, bukan untuk dipercaya.
    ///
    /// Panel tetap menyematkan sidik jari yang benar-benar dilihatnya saat
    /// menyambung. Nilai di sini semata-mata agar asisten dan dosen dapat
    /// membandingkannya dengan mata — mempercayainya sama saja mempercayai
    /// siapa pun yang bisa mengirim datagram.
    /// </summary>
    public string? Thumbprint { get; init; }
}

/* ================= identitas dan wewenang ================= */

public enum PanelRole
{
    /// <summary>Belum disetujui Superadmin. Tidak boleh melakukan apa pun.</summary>
    Pending,

    /// <summary>Mengajar: siaran, chat, kunci layar, kendali jarak jauh, mode ujian.</summary>
    Dosen,

    /// <summary>Merawat mesin: seluruh wewenang Dosen ditambah daya, pendaftaran, konfigurasi.</summary>
    Asisten,

    /// <summary>Panel yang menyatu dengan koordinator. Menerbitkan dan mencabut kredensial.</summary>
    Superadmin,
}

/// <summary>
/// Wewenang sebagai bendera, bukan diturunkan dari peran di sisi panel.
///
/// Penegakannya ada di Superadmin; daftar ini dikirim ke panel semata-mata agar
/// tombol yang tidak boleh dipakai dapat disembunyikan. Panel yang menyembunyikan
/// tombol tanpa penegakan di server hanyalah dekorasi — siapa pun yang mengubah
/// pesannya bisa melewatinya.
/// </summary>
[Flags]
public enum PanelRights
{
    None = 0,
    Broadcast = 1 << 0,
    Chat = 1 << 1,
    LockScreen = 1 << 2,
    RemoteControl = 1 << 3,
    FileShare = 1 << 4,
    ExamMode = 1 << 5,
    PowerControl = 1 << 6,
    ManageUnits = 1 << 7,
    Configure = 1 << 8,
    ManageCredentials = 1 << 9,

    Dosen = Broadcast | Chat | LockScreen | RemoteControl | FileShare | ExamMode,
    Asisten = Dosen | PowerControl | ManageUnits | Configure,
    Superadmin = Asisten | ManageCredentials,
}

public static class PanelRightsExtensions
{
    /// <summary>
    /// Wewenang tunggal saja, tanpa gabungan seperti Dosen atau Superadmin.
    ///
    /// ADA KARENA ToString() PADA ENUM BENDERA BERBOHONG DENGAN RAPI.
    ///
    /// Bila nilainya kebetulan sama persis dengan sebuah anggota bernama,
    /// ToString() mengembalikan nama itu SAJA. PanelRights.Superadmin
    /// menghasilkan "Superadmin" — bukan "Broadcast, Chat, LockScreen, ...".
    ///
    /// Panel yang mengurai teks itu lalu menyimpulkan pemegang wewenang
    /// tertinggi tidak boleh menyiarkan, tidak boleh chat, tidak boleh apa pun.
    /// Menunya lenyap, dan tidak ada satu pun galat: hasilnya memang string
    /// yang sah, hanya bukan yang dikira.
    ///
    /// Karena itu yang dikirim ke panel adalah daftar wewenang tunggal, disusun
    /// eksplisit, bukan hasil pemformatan enum.
    /// </summary>
    public static string[] ToAtomicNames(this PanelRights rights) =>
    [
        .. Enum.GetValues<PanelRights>()
            .Where(r => r != PanelRights.None
                     && r != PanelRights.Dosen
                     && r != PanelRights.Asisten
                     && r != PanelRights.Superadmin
                     && rights.HasFlag(r))
            .Select(r => r.ToString())
    ];
}

public sealed record PanelHello
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string MachineName { get; init; }
    public required string AppVersion { get; init; }

    /// <summary>Token milik perangkat ini, pada sambungan berikutnya.</summary>
    public string? DeviceToken { get; init; }

    /// <summary>
    /// Peran yang DIMINTA panel, bukan yang dimilikinya.
    ///
    /// Ditampilkan kepada penyetuju sebagai permintaan, dan hanya itu. Yang
    /// menentukan tetap orang di PC Asisten.
    ///
    /// Kalau nilai ini mengikat, kode pendaftaran yang bocor langsung berarti
    /// wewenang Asisten bagi siapa pun yang memegangnya — dan persetujuan
    /// menjadi stempel atas keputusan yang sudah diambil pemohonnya sendiri.
    /// Kalau ia tidak ditampilkan sama sekali, penyetuju harus menebak siapa
    /// yang baru muncul di daftarnya. Ditampilkan sebagai permintaan, keduanya
    /// terhindar.
    /// </summary>
    public PanelRole RequestedRole { get; init; } = PanelRole.Dosen;

    /// <summary>
    /// True bila panel ini menyala lewat kabel, false bila lewat Wi-Fi.
    ///
    /// Bukan basa-basi: siaran 1080p menuntut sekitar 45 Mbps uplink, dan panel
    /// di Wi-Fi perlu diperingatkan sebelum memulainya. Superadmin yang mencatat
    /// ini supaya asisten dapat melihat mengapa siaran seorang dosen tersendat.
    /// </summary>
    public bool WiredConnection { get; init; }
}

public sealed record PanelWelcome
{
    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }

    /// <summary>Diterbitkan sekali saat pendaftaran pertama, lalu disimpan panel.</summary>
    public string? IssuedDeviceToken { get; init; }

    /// <summary>
    /// Sidik jari sertifikat menurut koordinator sendiri.
    ///
    /// UNTUK DILIHAT MANUSIA, BUKAN UNTUK DIPERCAYA MESIN.
    ///
    /// Yang disematkan panel adalah sidik jari yang benar-benar dilihatnya pada
    /// sambungan, bukan nilai ini — mempercayai nilai kiriman akan membuat
    /// penyematan membuktikan dirinya sendiri.
    ///
    /// Gunanya: ditampilkan berdampingan di layar dosen dan layar asisten, agar
    /// keduanya dapat memastikan mereka sedang membicarakan mesin yang sama
    /// sebelum persetujuan ditekan.
    /// </summary>
    public string? SuperadminProof { get; init; }

    public PanelRole Role { get; init; } = PanelRole.Pending;
    public PanelRights Rights { get; init; } = PanelRights.None;

    /// <summary>Nama lab, untuk ditampilkan di panel agar tidak salah subnet.</summary>
    public string LabName { get; init; } = "Laboratorium";
}

/* ================= perintah ================= */

/// <summary>
/// Satu amplop untuk seluruh perintah ke agent.
///
/// Sebelumnya panel memanggil hub sepuluh tempat berbeda, masing-masing
/// mengurus sendiri pencarian connection id, pelacakan batch, dan pencatatan
/// audit. Tiga hal yang harus benar di sepuluh tempat berarti sepuluh peluang
/// salah, dan yang paling sering terlewat adalah auditnya — justru bagian yang
/// baru dicari orang ketika sesuatu sudah terlanjur terjadi.
///
/// Di sini ketiganya dikerjakan sekali, di satu tempat, oleh Superadmin.
/// </summary>
public sealed record PanelDispatch
{
    public required AgentCommand Command { get; init; }

    /// <summary>Unit tujuan. Kosong berarti seluruh unit yang tersambung.</summary>
    public string[]? Targets { get; init; }

    /// <summary>Label untuk jejak audit, misalnya "screen.lock" atau "power.off".</summary>
    public required string Action { get; init; }
}

public sealed record PanelDispatchResult
{
    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }

    /// <summary>Jumlah unit yang benar-benar dikirimi, bukan yang diminta.</summary>
    public int Reached { get; init; }

    public Guid BatchId { get; init; }
}

/* ================= keadaan yang didorong ke panel ================= */

/// <summary>
/// Unit sebagaimana dilihat panel.
///
/// Sengaja berupa DTO terpisah dari keadaan internal Superadmin. Mengirim objek
/// internal apa adanya membuat setiap perubahan di dalam ikut bocor ke seluruh
/// panel, dan panel versi lama akan pecah oleh perubahan yang tidak ada
/// hubungannya dengan mereka.
/// </summary>
public sealed record UnitSnapshot
{
    public required string UnitId { get; init; }

    /// <summary>
    /// Nama pilihan asisten, misalnya "Laptop Bu Rina". Dikunci ke token
    /// perangkat, bukan ke alamat IP — alias yang dikunci ke IP akan berpindah
    /// orang begitu DHCP memberi alamat yang sama kepada laptop lain.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// Nama yang diketik sendiri mahasiswa di unitnya, misalnya "Laptop Sinta".
    ///
    /// TERPISAH DARI ALIAS, dan urutan pemakaiannya tetap: alias dulu, lalu ini,
    /// lalu unit id. Asisten yang sudah menata denah lab tidak akan kehilangan
    /// penamaannya karena mahasiswa mengetik sesuatu, dan laptop pribadi
    /// bernama "DESKTOP-4F9K2M1" tidak lagi harus dinamai satu per satu dari
    /// panel hanya supaya dapat dikenali.
    /// </summary>
    public string? SelfName { get; init; }

    /// <summary>
    /// Unit ini membuka siaran langsung dalam layar penuh.
    ///
    /// Dinyalakan untuk PC yang tercolok proyektor. Dosen yang menyiarkan dari
    /// laptopnya tidak perlu berjalan ke PC dosen untuk membuka jendelanya —
    /// dan tidak perlu memindahkan kabel HDMI, yang justru pekerjaan yang ingin
    /// dihapus aplikasi ini.
    /// </summary>
    public bool AutoFullscreen { get; init; }

    public required UnitStatus Status { get; init; }
    public string IpAddress { get; init; } = "";
    public string HostName { get; init; } = "";

    /// <summary>
    /// MAC unit, dipakai Wake-on-LAN — dan hanya berguna bila terlihat.
    ///
    /// Paket ajaib dialamatkan ke MAC, bukan ke IP. Ketika tombol "nyalakan"
    /// tidak membangunkan sebuah PC, penyebabnya hampir selalu MAC yang kosong
    /// atau salah kartu jaringan, dan tidak ada satu pun layar yang menampilkan
    /// nilai itu. Asisten hanya melihat perintah yang tampak berhasil dan PC
    /// yang tetap mati.
    /// </summary>
    public string MacAddress { get; init; } = "";

    /// <summary>Kabel atau nirkabel, diukur di unit — koordinator tidak dapat mengetahuinya.</summary>
    public bool WiredConnection { get; init; }

    /// <summary>Kecepatan tautan Mbps; 0 berarti adapter tidak melaporkannya.</summary>
    public int LinkSpeedMbps { get; init; }

    public string NetworkAdapter { get; init; } = "";

    /// <summary>
    /// Alamat koordinator yang dipakai unit ini untuk sampai. Unit dengan
    /// alamat yang sama berbagi jalur — sedekat mungkin dengan "switch yang
    /// sama" tanpa perangkat terkelola.
    /// </summary>
    public string CoordinatorHost { get; init; } = "";

    /// <summary>Waktu pulang-pergi milidetik; null berarti belum atau tidak terukur.</summary>
    public double? LatencyMs { get; init; }

    public DateTimeOffset LastSeen { get; init; }

    public string? ThumbnailDataUrl { get; init; }
    public DateTimeOffset? HelpRequestedAt { get; init; }
    public int Unread { get; init; }

    /// <summary>Perangkat pribadi mahasiswa, bukan PC lab. Wewenang atasnya lebih sempit.</summary>
    public bool IsGuest { get; init; }

    /// <summary>
    /// Diblokir asisten: tidak diizinkan menyambung ke lab sama sekali.
    ///
    /// MEDAN INI SEMPAT BERBOHONG SELAMA BERBULAN-BULAN.
    ///
    /// Ia dideklarasikan sejak awal beserta komentar yang menjanjikan "tidak
    /// menerima siaran maupun berkas", tetapi tidak ada satu pun tempat yang
    /// mengisinya dan tidak ada satu pun tempat yang membacanya. Nilainya selalu
    /// false, jadi janjinya tidak pernah teruji dan tidak pernah gagal dengan
    /// cara yang terlihat.
    ///
    /// Sejak M7.6 ia benar-benar berasal dari kolom blocked di basis data, dan
    /// blokirnya ditegakkan di UnitStore.Validate — diperiksa sebelum token
    /// maupun kode pendaftaran, sehingga menghapus agent.json tidak membuka
    /// jalan masuk kembali.
    /// </summary>
    public bool Blocked { get; init; }

    public string? StudentNim { get; init; }
    public string? StudentName { get; init; }
}

public sealed record ActivityEntryDto(
    string? UnitId, string Actor, string Action, string? Detail, DateTimeOffset CreatedAt);

/* ================= relay siaran ================= */

/// <summary>
/// Panel meminta izin menyiarkan, dan menerima alamat tujuan aliran.
///
/// Panel TIDAK pernah memancarkan multicast sendiri. Ia mengirim satu aliran
/// unicast ke Superadmin, dan Superadmin yang memancarkannya ke 40 unit.
///
/// Alasannya bukan kerapian melainkan fisika jaringan: panel di Wi-Fi yang
/// memancarkan multicast memaksa access point menyiarkan ulang seluruh aliran
/// itu ke udara pada laju dasar terendah — sehingga laptop dosen berebut
/// airtime dengan siarannya sendiri yang dipantulkan kembali kepadanya.
///
/// Sebagai efek samping, relay ini juga menjadi tempat alami menegakkan aturan
/// "satu sumber pada satu waktu", karena semuanya lewat satu titik.
/// </summary>
public sealed record BroadcastClaim
{
    public required int Width { get; init; }
    public required int FrameRate { get; init; }
    public required int Quality { get; init; }
    public required string SourceLabel { get; init; }

    /// <summary>Unit tujuan; kosong berarti seluruh unit yang tersambung.</summary>
    public string[]? Targets { get; init; }

    /// <summary>True untuk merebut siaran yang sedang berjalan milik panel lain.</summary>
    public bool Takeover { get; init; }
}

public sealed record BroadcastGrant
{
    public required bool Granted { get; init; }

    /// <summary>Diisi bila ditolak: siapa yang sedang menyiarkan.</summary>
    public string? HeldBy { get; init; }

    /// <summary>Alamat dan port tujuan aliran unicast dari panel ke Superadmin.</summary>
    public string? RelayHost { get; init; }
    public int RelayPort { get; init; }

    /// <summary>
    /// Peringatan yang perlu ditampilkan sebelum siaran dimulai, misalnya bahwa
    /// panel tersambung lewat Wi-Fi pada resolusi yang menuntut 45 Mbps uplink.
    ///
    /// Diberitahukan, bukan diturunkan diam-diam: dosen yang mendapati kualitas
    /// berbeda dari kemarin tanpa ada yang mengubah apa pun akan menyimpulkan
    /// aplikasinya rusak.
    /// </summary>
    public string? Warning { get; init; }
}

/// <summary>Keadaan siaran, didorong ke seluruh panel supaya semuanya sepakat.</summary>
public sealed record BroadcastState
{
    public required bool Active { get; init; }
    public string? SourceLabel { get; init; }
    public string? HeldByPanel { get; init; }
    public int Receivers { get; init; }
    public double Mbps { get; init; }
}
