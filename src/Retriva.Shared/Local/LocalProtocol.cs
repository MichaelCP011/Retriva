using System.Text.Json.Serialization;
using Retriva.Shared.Protocol;

namespace Retriva.Shared.Local;

/// <summary>
/// Protokol lokal antara RetrivaAgent (layanan, SYSTEM, Session 0) dan
/// RetrivaClient (aplikasi di sesi pengguna).
///
/// Terpisah dari protokol konsol karena persoalannya berbeda: yang ini
/// menyeberangi batas sesi Windows di satu mesin, tanpa jaringan, tanpa
/// autentikasi jarak jauh. Layanan tidak dapat menampilkan jendela maupun
/// menyuntikkan input; klien inilah tangannya di sesi pengguna.
/// </summary>
public static class LocalProtocol
{
    /// <summary>Pipa bernama, terlihat lintas sesi di mesin yang sama.</summary>
    public const string PipeName = "Retriva.Agent.Session";

    /// <summary>Pesan dipisahkan baris baru agar mudah dibaca saat menelusuri masalah.</summary>
    public const char Delimiter = '\n';

    public const int ReconnectSeconds = 3;
}

/* ---------------- Klien → Agent ---------------- */

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$msg")]
[JsonDerivedType(typeof(ClientHello),       "hello")]
[JsonDerivedType(typeof(ClientAck),         "ack")]
[JsonDerivedType(typeof(StudentSignedIn),   "student.signin")]
[JsonDerivedType(typeof(StudentSignedOut),  "student.signout")]
[JsonDerivedType(typeof(RemoteSessionEnded),"remote.ended")]
[JsonDerivedType(typeof(StudentChat),       "chat.send")]
[JsonDerivedType(typeof(StudentHelp),       "help.raise")]
[JsonDerivedType(typeof(UnitSelfName),      "unit.selfname")]
[JsonDerivedType(typeof(BroadcastStats),    "broadcast.stats")]
[JsonDerivedType(typeof(ExamObservation),  "exam.observed")]
[JsonDerivedType(typeof(ScreenThumbnailReport), "screen.thumbnail")]
public abstract record ClientMessage
{
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ClientHello : ClientMessage
{
    public required string WindowsUser { get; init; }
    public required int SessionId { get; init; }
    public required string ClientVersion { get; init; }
    /// <summary>Folder Documents milik pengguna Windows yang login di sesi ini.</summary>
    public string? DocumentsDirectory { get; init; }
}

/// <summary>Balasan atas perintah dari agent, supaya hasilnya bisa diteruskan ke konsol.</summary>
public sealed record ClientAck : ClientMessage
{
    public required Guid CommandId { get; init; }
    public required bool Success { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Identitas Retriva, terpisah dari akun Windows yang dipakai autologon.</summary>
public sealed record StudentSignedIn : ClientMessage
{
    public required string Nim { get; init; }
    public required string DisplayName { get; init; }
    public bool IsGuest { get; init; }
}

public sealed record StudentSignedOut : ClientMessage;

/// <summary>
/// Laporan penerimaan siaran dari klien ke agent.
///
/// ARAHNYA SENGAJA DARI BAWAH KE ATAS.
///
/// Multicast UDP tidak punya jalur balik: pengirim tidak akan pernah tahu siapa
/// yang mendengarkan. Jadi satu-satunya cara menjawab "berapa yang menerima
/// siaran saya" dengan jujur adalah menanyakannya kepada yang menerima.
///
/// Dikirim hanya selagi penonton terbuka, dan hanya beberapa detik sekali —
/// empat puluh unit yang melapor tiap detik akan menambah lalu lintas kendali
/// tanpa menambah ketepatan yang berarti bagi mata manusia.
/// </summary>
public sealed record BroadcastStats : ClientMessage
{
    /// <summary>Frame yang sudah dirakit utuh sejak penonton dibuka.</summary>
    public required long FramesComplete { get; init; }

    /// <summary>Paket yang tiba. Nol berarti multicast tidak sampai sama sekali.</summary>
    public required long PacketsReceived { get; init; }
}

/// <summary>
/// Sesi kendali berakhir dari sisi klien, bukan atas permintaan admin.
///
/// Perlu jalur sendiri karena penyebabnya bukan perintah: mahasiswa menekan
/// pintasan keluar, atau penangkapan layar gagal. Tanpa pesan ini panel admin
/// akan terus menampilkan frame terakhir dan tampak seperti sesi yang masih
/// hidup — kegagalan yang tidak bersuara.
/// </summary>
public sealed record RemoteSessionEnded : ClientMessage
{
    public required string Reason { get; init; }
}

/// <summary>Pesan yang diketik mahasiswa, diteruskan agent ke konsol.</summary>
public sealed record StudentChat : ClientMessage
{
    public required string Text { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();
}

/// <summary>
/// Satu pengamatan dari klien selama sesi ujian.
///
/// DIKIRIM DARI KLIEN, BUKAN DARI AGENT, dan itu bukan pilihan gaya.
///
/// Agent berjalan sebagai layanan di Session 0. Ia tidak dapat membaca jendela
/// aktif milik sesi pengguna, tidak dapat membuka papan klip, dan tidak dapat
/// menerima WM_DEVICECHANGE — ketiganya milik sesi desktop. Pembagian yang sama
/// berlaku untuk kunci layar, tangkapan layar, dan siaran.
///
/// Konsekuensinya perlu disadari: mahasiswa yang mematikan RetrivaClient
/// menghentikan pemantauan. Yang tidak dapat ia sembunyikan adalah kepergian
/// kliennya sendiri — agent melaporkannya ke panel, dan lini masa ujian akan
/// berisi lubang yang justru lebih kentara daripada aktivitas apa pun.
/// </summary>
public sealed record ExamObservation : ClientMessage
{
    public required ExamEventKind Kind { get; init; }
    public required string Description { get; init; }
}

/// <summary>Mahasiswa menekan tombol minta bantuan, atau membatalkannya.</summary>
public sealed record StudentHelp : ClientMessage
{
    public required bool Raised { get; init; }
}

/// <summary>
/// Nama yang diketik mahasiswa untuk unit tempat ia duduk. Kosong berarti dihapus.
///
/// USULAN, BUKAN PENGGANTIAN NAMA.
///
/// Nama mesin Windows di laptop pribadi biasanya tidak berguna bagi siapa pun —
/// "DESKTOP-4F9K2M1" tidak memberi tahu asisten siapa yang duduk di mana. Yang
/// tahu jawabannya adalah orang yang duduk di depannya, dan sampai sekarang ia
/// tidak punya cara memberi tahu.
///
/// Yang dikirim ke sini disimpan di kolomnya sendiri dan TIDAK menyentuh alias
/// yang ditetapkan asisten. Panel menampilkan alias bila ada, lalu nama ini,
/// lalu unit id — jadi mahasiswa hanya mengisi kekosongan dan tidak pernah
/// menimpa penataan lab yang sudah disusun.
/// </summary>
public sealed record UnitSelfName : ClientMessage
{
    public required string Name { get; init; }
}

/// <summary>
/// Pratinjau berkala dari klien di sesi desktop interaktif.
/// Agent berjalan di Session 0 dan tidak dapat menangkap layar secara langsung,
/// sehingga tangkapan diambil oleh klien di sesi pengguna.
/// </summary>
public sealed record ScreenThumbnailReport : ClientMessage
{
    public required byte[] JpegData { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/* ---------------- Agent → Klien ---------------- */

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$msg")]
[JsonDerivedType(typeof(AgentWelcome),   "welcome")]
[JsonDerivedType(typeof(ShowLockScreen), "lock.show")]
[JsonDerivedType(typeof(HideLockScreen), "lock.hide")]
[JsonDerivedType(typeof(ShowBroadcast),  "broadcast.show")]
[JsonDerivedType(typeof(HideBroadcast),  "broadcast.hide")]
[JsonDerivedType(typeof(BeginPresenting),    "present.begin")]
[JsonDerivedType(typeof(EndPresenting),      "present.end")]
[JsonDerivedType(typeof(BeginRemoteSession), "remote.begin")]
[JsonDerivedType(typeof(EndRemoteSession),   "remote.end")]
[JsonDerivedType(typeof(InjectInput),        "remote.input")]
[JsonDerivedType(typeof(DeliverChat),        "chat.deliver")]
[JsonDerivedType(typeof(HelpAcknowledged),   "help.ack")]
[JsonDerivedType(typeof(ClearChat),          "chat.clear")]
[JsonDerivedType(typeof(FileArrived),        "file.arrived")]
[JsonDerivedType(typeof(ApplyAccessPolicy),  "policy.apply")]
[JsonDerivedType(typeof(BeginExamWatch),     "exam.begin")]
[JsonDerivedType(typeof(EndExamWatch),       "exam.end")]
public abstract record SessionMessage
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AgentWelcome : SessionMessage
{
    public required string UnitId { get; init; }
    public required bool ConsoleConnected { get; init; }

    /// <summary>
    /// Aturan akses yang berlaku, ikut di pesan pertama.
    ///
    /// Klien tidak boleh menampilkan layar masuk lebih dulu lalu menyesuaikan
    /// diri setelah aturannya tiba. Mahasiswa akan sempat melihat kolom NIM
    /// yang kemudian hilang sendiri — dan yang terlihat seperti kedipan itu
    /// sebenarnya keadaan setengah jadi yang bisa diketik.
    /// </summary>
    public AccessPolicy Policy { get; init; } = new();

    /// <summary>
    /// Folder tujuan berkas, DIKATAKAN agent alih-alih ditebak klien.
    ///
    /// Agent yang mengunduh, jadi agent yang tahu ke mana berkasnya ditulis —
    /// termasuk bila folder yang dipilih saat pemasangan ternyata tidak dapat
    /// dipakai dan ia jatuh kembali ke folder bawaan.
    ///
    /// Sebelum ini kedua sisi menghitungnya sendiri dengan rumus yang kebetulan
    /// sama. Rumus kembar itu tetap benar sampai salah satunya berubah — dan
    /// bila itu terjadi, gejalanya adalah daftar berkas mahasiswa yang kosong
    /// padahal unduhannya sukses, dan tombol "Buka folder" yang membuka folder
    /// kosong yang salah. Tidak ada galat di mana pun.
    /// </summary>
    public string FileInbox { get; init; } = "";
}

/// <summary>
/// Aturan akses berubah selagi klien berjalan.
///
/// Terpisah dari AgentWelcome karena waktunya berbeda: yang satu menyertai
/// sambungan baru, yang ini datang di tengah sesi. Menyatukannya berarti klien
/// memperlakukan perubahan aturan sebagai sambungan ulang, dan mahasiswa yang
/// sedang mengetik akan terlempar kembali ke layar masuk.
/// </summary>
public sealed record ApplyAccessPolicy : SessionMessage
{
    public required AccessPolicy Policy { get; init; }
}

public sealed record ShowLockScreen : SessionMessage
{
    public string Message { get; init; } = "Layar dikunci oleh Admin";
    /// <summary>Nama yang ditampilkan agar mahasiswa tahu harus menghubungi siapa.</summary>
    public string IssuedBy { get; init; } = "Admin";
}

public sealed record HideLockScreen : SessionMessage;

/// <summary>
/// Menyuruh klien bergabung ke grup multicast dan menampilkan siaran.
/// Byte videonya sendiri tidak lewat pipa ini — hanya perintahnya.
/// </summary>
public sealed record ShowBroadcast : SessionMessage
{
    public required string MulticastGroup { get; init; }
    public required int Port { get; init; }
    public string SourceLabel { get; init; } = "Admin";
    public bool Fullscreen { get; init; }
}

public sealed record HideBroadcast : SessionMessage;

/// <summary>
/// Meminta klien menyiarkan layarnya sendiri ke relay Superadmin.
///
/// Yang menangkap layar adalah KLIEN, bukan agent. Agent berjalan sebagai
/// layanan tanpa sesi desktop; ia tidak dapat melihat satu piksel pun dari
/// layar mahasiswa. Pembagian yang sama berlaku untuk kunci layar dan kendali
/// jarak jauh.
/// </summary>
public sealed record BeginPresenting : SessionMessage
{
    public required string RelayHost { get; init; }
    public required int RelayPort { get; init; }

    public int Width { get; init; } = 1600;
    public int FrameRate { get; init; } = 15;
    public int Quality { get; init; } = 78;

    /// <summary>Nama yang muncul di penanda, supaya mahasiswa tahu siapa yang memintanya.</summary>
    public string RequestedBy { get; init; } = "Admin";
}

public sealed record EndPresenting : SessionMessage;

/// <summary>
/// Menyuruh klien mulai memantau, dan menampilkan bahwa ia memantau.
///
/// Kedua hal itu satu perintah dan tidak boleh dipisah. Pemantauan yang menyala
/// tanpa papannya tampil adalah pengawasan diam-diam; papan yang tampil tanpa
/// pemantauan menyala adalah kebohongan ke arah sebaliknya. Menyatukannya dalam
/// satu pesan membuat kedua keadaan itu mustahil.
/// </summary>
public sealed record BeginExamWatch : SessionMessage
{
    public required long SessionId { get; init; }
    public required string Title { get; init; }
}

public sealed record EndExamWatch : SessionMessage;

/// <summary>
/// Menyuruh klien mulai mengirim layarnya ke admin dan menerima suntikan input.
///
/// Arahnya berlawanan dengan siaran: di sini klien yang menjadi pengirim dan
/// admin yang menonton, jadi tujuannya unicast ke satu alamat, bukan multicast.
/// </summary>
public sealed record BeginRemoteSession : SessionMessage
{
    /// <summary>Alamat panel admin yang akan menerima aliran layar.</summary>
    public required string AdminHost { get; init; }
    public required int Port { get; init; }

    public int FrameRate { get; init; } = 15;

    /// <summary>False berarti admin hanya melihat, tanpa mengambil alih input.</summary>
    public bool AllowInput { get; init; }

    /// <summary>
    /// Sesi menunggu persetujuan mahasiswa sebelum dimulai.
    ///
    /// Diputuskan koordinator, bukan klien, supaya aturannya sama untuk seluruh
    /// lab. Klien yang memutuskan sendiri berarti satu PC dapat menuntut izin
    /// sementara yang lain tidak, dan tidak ada satu tempat pun yang menyatakan
    /// mana yang benar.
    /// </summary>
    public bool NeedsConsent { get; init; }

    /// <summary>Nama yang ditampilkan di indikator agar mahasiswa tahu siapa.</summary>
    public string IssuedByName { get; init; } = "Admin";
}

public sealed record EndRemoteSession : SessionMessage;

/// <summary>
/// Satu peristiwa mouse atau keyboard untuk disuntikkan di sisi mahasiswa.
///
/// KOORDINATNYA PER-SEPULUH-RIBU, bukan piksel. Admin melihat JPEG yang sudah
/// diperkecil dan tidak tahu resolusi fisik layar mahasiswa; mengirim piksel
/// berarti admin harus menebak, dan tebakan itu akan salah pada layar dengan
/// penskalaan DPI berbeda. Dengan pecahan, klien yang melakukan konversi —
/// dan hanya klien yang benar-benar tahu ukuran layarnya sendiri.
/// </summary>
public sealed record InjectInput : SessionMessage
{
    public required RemoteInputKind Kind { get; init; }

    /// <summary>0..10000 dari kiri ke kanan layar mahasiswa.</summary>
    public int X { get; init; }
    /// <summary>0..10000 dari atas ke bawah layar mahasiswa.</summary>
    public int Y { get; init; }

    /// <summary>Virtual-key code Windows untuk peristiwa keyboard.</summary>
    public int KeyCode { get; init; }

    /// <summary>Kelipatan 120 seperti konvensi WM_MOUSEWHEEL.</summary>
    public int WheelDelta { get; init; }

    /// <summary>Tombol mouse: 0 kiri, 1 tengah, 2 kanan.</summary>
    public int Button { get; init; }
}

public enum RemoteInputKind
{
    MouseMove, MouseDown, MouseUp, MouseWheel, KeyDown, KeyUp,
}

/// <summary>Pesan dari admin untuk ditampilkan di klien.</summary>
public sealed record DeliverChat : SessionMessage
{
    public required string Text { get; init; }
    public required string FromDisplayName { get; init; }

    /// <summary>
    /// True bila pesan ini disebar ke seluruh kelas, bukan ditujukan ke satu
    /// mahasiswa. Klien menandainya berbeda supaya mahasiswa tidak mengira
    /// pengumuman kelas adalah teguran pribadi.
    /// </summary>
    public bool ToEveryone { get; init; }
}

/// <summary>
/// Admin sudah menangani permintaan bantuan, jadi penandanya dipadamkan.
///
/// Dikirim balik ke klien, bukan hanya dibersihkan di konsol, supaya mahasiswa
/// tahu permintaannya terlihat. Tanpa umpan balik ini ia akan menekan tombolnya
/// berulang kali karena tidak ada yang berubah di layarnya.
/// </summary>
public sealed record HelpAcknowledged : SessionMessage
{
    public string By { get; init; } = "Admin";
}

/// <summary>Mengosongkan tampilan percakapan di sisi mahasiswa.</summary>
public sealed record ClearChat : SessionMessage;

/// <summary>
/// Berkas sudah selesai diunduh DAN terverifikasi utuh.
///
/// Dikirim hanya setelah sidik SHA-256 cocok. Memberi tahu mahasiswa saat
/// unduhan baru dimulai akan membuatnya membuka berkas yang belum lengkap.
/// </summary>
public sealed record FileArrived : SessionMessage
{
    public required string FileName { get; init; }
    public required string SavedPath { get; init; }
    public required long SizeBytes { get; init; }
}
