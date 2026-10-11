namespace Retriva.Shared.Protocol;

/// <summary>Permintaan pendaftaran, dikirim agent segera setelah kanal TLS terbentuk.</summary>
public sealed record RegisterRequest
{
    /// <summary>Nomor unit hasil deteksi agent, mis. "PC-12".</summary>
    public required string UnitId { get; init; }
    public required string HostName { get; init; }
    public required string IpAddress { get; init; }
    public required string MacAddress { get; init; }
    public required string AgentVersion { get; init; }

    /// <summary>Dipakai hanya pada sambungan pertama, sebelum unit punya token.</summary>
    public string? EnrollmentSecret { get; init; }
    /// <summary>Dipakai pada sambungan berikutnya, menggantikan enrollment secret.</summary>
    public string? DeviceToken { get; init; }

    /// <summary>
    /// Tautan jaringan unit ini, DIUKUR DI UNIT.
    ///
    /// Koordinator tidak dapat mengetahuinya. Ia hanya melihat alamat asal
    /// paket; apakah paket itu datang lewat kabel atau udara, dan pada
    /// kecepatan berapa, hanya diketahui mesin yang mengirimkannya.
    ///
    /// Inilah bedanya dengan diagram topologi yang digantikan: switch tidak
    /// dapat dideteksi, tetapi jenis tautan dapat — jadi yang digambar hanya
    /// yang memang terukur.
    /// </summary>
    public bool WiredConnection { get; init; }

    /// <summary>Kecepatan tautan dalam Mbps, 0 bila tidak dilaporkan adapter.</summary>
    public int LinkSpeedMbps { get; init; }

    /// <summary>Nama adapter, untuk menelusuri MAC yang ternyata milik kartu lain.</summary>
    public string NetworkAdapter { get; init; } = "";
}

/// <summary>
/// Setelan akses yang berlaku di seluruh lab, ditentukan koordinator.
///
/// DIKIRIM, BUKAN DIBACA SENDIRI OLEH KLIEN.
///
/// Ketiga nilainya tinggal di console.json, yang hanya ada di mesin
/// koordinator. PC mahasiswa tidak punya berkas itu dan tidak boleh punya —
/// setelan yang disalin ke empat puluh mesin adalah empat puluh tempat ia bisa
/// berbeda, dan yang berbeda tidak akan ketahuan sampai satu PC berperilaku
/// lain dari yang lain di tengah praktikum.
///
/// Sebelum ini tidak ada jalur sama sekali. Ketiga saklarnya tersimpan rapi,
/// muncul kembali dengan benar setelah aplikasi dibuka ulang, dan tidak
/// berpengaruh pada satu unit pun.
/// </summary>
public sealed record AccessPolicy
{
    /// <summary>Mahasiswa harus memasukkan NIM dan nama sebelum memakai PC.</summary>
    public bool RequireStudentLogin { get; init; } = true;

    /// <summary>Boleh masuk tanpa NIM, ditandai sebagai tamu di panel.</summary>
    public bool AllowGuestAccess { get; init; } = true;

    /// <summary>Kendali jarak jauh menunggu persetujuan mahasiswa lebih dulu.</summary>
    public bool StudentsMayRejectControl { get; init; }
}

public sealed record RegisterResponse
{
    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    /// <summary>Diterbitkan sekali saat pendaftaran pertama, lalu disimpan agent.</summary>
    public string? IssuedDeviceToken { get; init; }
    /// <summary>Nomor unit yang diakui konsol; bisa berbeda bila deteksi agent salah.</summary>
    public string? ConfirmedUnitId { get; init; }
    public int HeartbeatSeconds { get; init; } = 5;
    public int ThumbnailSeconds { get; init; } = 2;

    /// <summary>
    /// Lebar pratinjau yang diminta konsol. Tingginya diturunkan dari rasio
    /// 16:9, jadi hanya satu angka yang perlu disepakati kedua sisi.
    /// </summary>
    public int ThumbnailWidth { get; init; } = 480;

    /// <summary>
    /// Setelan akses yang berlaku saat unit ini mendaftar.
    ///
    /// Ikut di sini supaya klien tahu aturannya sebelum mahasiswa pertama
    /// menyentuh papan ketik. Perubahan berikutnya didorong lewat pesan
    /// "Policy" — tanpa itu, mengubah saklar baru terasa setelah seluruh lab
    /// dinyalakan ulang.
    /// </summary>
    public AccessPolicy Policy { get; init; } = new();
}

/// <summary>Denyut berkala dari agent — dasar penentuan status online/offline.</summary>
public sealed record HeartbeatReport
{
    public required string UnitId { get; init; }
    public required UnitStatus Status { get; init; }
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Nama pengguna Windows yang sedang login, null bila belum ada.</summary>
    public string? LoggedOnUser { get; init; }

    /// <summary>NIM mahasiswa yang login di aplikasi Retriva, terpisah dari akun Windows.</summary>
    public string? RetrivaUserNim { get; init; }

    /// <summary>Nama mahasiswa yang login, sebagaimana ia menuliskannya sendiri.</summary>
    public string? RetrivaUserName { get; init; }

    /// <summary>Perangkat pribadi, bukan PC lab.</summary>
    public bool IsGuest { get; init; }

    /// <summary>
    /// Frame siaran yang benar-benar sudah dirakit utuh di unit ini.
    ///
    /// SATU-SATUNYA SUMBER JUJUR untuk "berapa yang menerima siaran saya".
    ///
    /// Koordinator tidak dapat mengetahuinya: multicast UDP tidak punya jalur
    /// balik, dan pengirim tidak pernah tahu siapa yang mendengarkan. Yang
    /// mengetahui hanya unit itu sendiri.
    ///
    /// Sebelum medan ini ada, panel menghitungnya sebagai "ada frame diteruskan
    /// atau tidak" lalu melaporkannya sebagai jumlah penerima — sehingga dosen
    /// yang menyiarkan ke empat puluh PC akan melihat angka 1.
    /// </summary>
    public long BroadcastFrames { get; init; }
}

/// <summary>Pratinjau kecil untuk grid dashboard. Sengaja JPEG, bukan video.</summary>
public sealed record ThumbnailFrame
{
    public required string UnitId { get; init; }
    public required byte[] JpegData { get; init; }
    public int Width { get; init; } = 240;
    public int Height { get; init; } = 135;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CommandResult
{
    public required Guid CommandId { get; init; }
    public required string UnitId { get; init; }
    public required CommandOutcome Outcome { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChatMessage
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public required string UnitId { get; init; }
    /// <summary>True bila dikirim admin, false bila dikirim mahasiswa.</summary>
    public required bool FromAdmin { get; init; }
    public required string DisplayName { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Kemajuan unduhan berkas di satu unit.
///
/// Dilaporkan berkala selama unduhan, bukan sekali di akhir. Untuk berkas 500 MB
/// ke 40 unit, laporan di akhir saja berarti panel diam berpuluh detik tanpa
/// bisa dibedakan dari pembagian yang macet — dan orang akan menekan tombolnya
/// lagi karena mengira tidak terjadi apa-apa.
/// </summary>
public sealed record FileTransferProgress
{
    public required string UnitId { get; init; }
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required long BytesReceived { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>menunggu, mengunduh, selesai, atau gagal.</summary>
    public required string Status { get; init; }
    public string? Detail { get; init; }

    public int Percent => SizeBytes <= 0
        ? 0
        : (int)Math.Clamp(BytesReceived * 100 / SizeBytes, 0, 100);
}

/// <summary>
/// Mahasiswa meminta bantuan asisten.
///
/// Terpisah dari chat meskipun bisa saja diwakili sebagai pesan biasa. Alasannya
/// bukan teknis melainkan perhatian: di lab 40 PC, permintaan bantuan harus
/// terlihat dari seberang ruangan tanpa siapa pun membaca daftar pesan. Sebagai
/// pesan biasa ia akan tenggelam di antara percakapan lain.
/// </summary>
public sealed record HelpRequest
{
    public required string UnitId { get; init; }
    /// <summary>False berarti permintaan dibatalkan mahasiswa.</summary>
    public required bool Raised { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Satu peristiwa yang tercatat selama sesi ujian berjalan.
///
/// CATATAN, BUKAN PELANGGARAN.
///
/// Bentuk sebelumnya bernama TestModeEvent, berkomentar "pelanggaran yang
/// terdeteksi", dan punya medan Severity. Tidak ada satu baris pun yang pernah
/// membuatnya — jadi janji itu tidak pernah teruji, dan tidak pernah gagal
/// dengan cara yang terlihat.
///
/// Severity dihapus dengan sengaja. Retriva dapat mengetahui bahwa jendela
/// berganti ke Chrome; ia tidak dapat mengetahui apakah itu menyontek.
/// Mahasiswa yang membuka Notepad saat ujian pemrograman sedang bekerja, bukan
/// melanggar. Menempelkan tingkat keparahan berarti perangkat lunak ini
/// menyimpulkan hal yang seharusnya disimpulkan pengajar — dan label itu akan
/// dibaca sebagai kesimpulan oleh siapa pun yang melihatnya, termasuk dalam
/// keadaan yang menentukan nilai seseorang.
///
/// Yang menggantikannya adalah Kind: jenis peristiwanya, tanpa penilaian.
/// </summary>
public sealed record ExamEvent
{
    public required string UnitId { get; init; }

    /// <summary>window · clipboard · usb. Jenis, bukan tingkat keparahan.</summary>
    public required ExamEventKind Kind { get; init; }

    /// <summary>
    /// Keterangan yang dapat dibaca manusia, disusun di klien.
    ///
    /// Untuk papan klip, ini TIDAK berisi apa yang disalin — hanya bahwa
    /// penyalinan terjadi, ukurannya, dan dari aplikasi apa. Menyimpan isinya
    /// berarti basis data lab menampung apa pun yang mahasiswa salin sepanjang
    /// ujian, termasuk kata sandi dan hal yang tidak berhubungan dengan ujian,
    /// pada berkas yang tidak terenkripsi di komputer asisten.
    /// </summary>
    public required string Description { get; init; }

    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum ExamEventKind
{
    /// <summary>Jendela aktif berganti; keterangannya berisi judul jendela.</summary>
    Window,

    /// <summary>Papan klip berubah. Isinya tidak pernah dicatat.</summary>
    Clipboard,

    /// <summary>Perangkat penyimpanan dicolokkan atau dilepas.</summary>
    Usb,
}

/// <summary>Sesi ujian yang sedang atau pernah berjalan.</summary>
/// <summary>
/// Satu baris daftar unit di layar mode ujian: berapa banyak, dan yang terakhir apa.
///
/// Sengaja TIDAK memuat penilaian apa pun — tidak ada tingkat keparahan, tidak
/// ada bendera, tidak ada skor. Keterangan terakhir adalah kalimat yang sama
/// dengan yang muncul di lini masa, dipindahkan ke depan supaya daftarnya dapat
/// dibaca sekilas. Yang menilai tetap orang yang membacanya.
/// </summary>
public sealed record ExamUnitSummary
{
    public required string UnitId { get; init; }
    public required int Count { get; init; }
    public required ExamEventKind LastKind { get; init; }
    public required string LastDescription { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
}

public sealed record ExamSession
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string StartedBy { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Null berarti sesi masih berjalan.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    public int EventCount { get; init; }
}
