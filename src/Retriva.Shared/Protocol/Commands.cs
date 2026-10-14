using System.Text.Json.Serialization;

namespace Retriva.Shared.Protocol;

/// <summary>
/// Jeda perintah daya, dalam detik. SATU-SATUNYA tempat angka ini ditulis.
///
/// Dialog konfirmasi di panel menyebutkan angka ini kepada operator, dan agent
/// memakainya sebagai jeda sungguhan. Sebelum konstanta ini ada, keduanya
/// menuliskan lima belas secara terpisah — dan yang berbohong adalah dialog
/// tentang mematikan empat puluh komputer.
/// </summary>
public static class PowerDelays
{
    /// <summary>Cukup lama untuk dibatalkan, cukup singkat untuk tidak ditunggu.</summary>
    public const int Shutdown = 15;

    /// <summary>Lebih singkat: restart tidak menghapus pekerjaan yang belum tersimpan
    /// dengan cara yang sama seperti mati total.</summary>
    public const int Restart = 5;
}

/// <summary>
/// Perintah dari konsol ke agent. Setiap fitur baru menambahkan satu turunan di sini,
/// satu handler di agent, dan satu pemicu di UI — tidak ada tempat lain yang perlu diubah.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
[JsonDerivedType(typeof(PingCommand),           "ping")]
[JsonDerivedType(typeof(LockScreenCommand),     "lock")]
[JsonDerivedType(typeof(UnlockScreenCommand),   "unlock")]
[JsonDerivedType(typeof(ShutdownCommand),       "shutdown")]
[JsonDerivedType(typeof(RestartCommand),        "restart")]
[JsonDerivedType(typeof(AbortShutdownCommand),  "power.abort")]
[JsonDerivedType(typeof(StartBroadcastCommand), "broadcast.start")]
[JsonDerivedType(typeof(StopBroadcastCommand),  "broadcast.stop")]
[JsonDerivedType(typeof(StartPresentingCommand),"present.start")]
[JsonDerivedType(typeof(StopPresentingCommand), "present.stop")]
[JsonDerivedType(typeof(BeginExamCommand),      "exam.begin")]
[JsonDerivedType(typeof(EndExamCommand),        "exam.end")]
[JsonDerivedType(typeof(FreezeBroadcastCommand),"broadcast.freeze")]
[JsonDerivedType(typeof(BeginRemoteCommand),    "remote.begin")]
[JsonDerivedType(typeof(EndRemoteCommand),      "remote.end")]
[JsonDerivedType(typeof(InjectInputCommand),    "remote.input")]
[JsonDerivedType(typeof(SendChatCommand),       "chat.send")]
[JsonDerivedType(typeof(AcknowledgeHelpCommand),"help.ack")]
[JsonDerivedType(typeof(ClearChatCommand),      "chat.clear")]
[JsonDerivedType(typeof(PushFileCommand),       "file.push")]
[JsonDerivedType(typeof(UpdateLabCommand),      "lab.update")]
public abstract record AgentCommand
{
    /// <summary>Dipakai agent untuk mengembalikan hasil yang cocok dengan perintahnya.</summary>
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Unit yang menerbitkan perintah, untuk keperluan audit.</summary>
    public string IssuedBy { get; init; } = string.Empty;
}

public sealed record PingCommand : AgentCommand;

public sealed record LockScreenCommand : AgentCommand
{
    /// <summary>Pesan yang ditampilkan di layar terkunci mahasiswa.</summary>
    public string Message { get; init; } = "Layar dikunci oleh Admin";
}

public sealed record UnlockScreenCommand : AgentCommand;

public sealed record ShutdownCommand : AgentCommand
{
    /// <summary>
    /// Jeda sebelum mesin benar-benar mati.
    ///
    /// Nilai bawaannya diambil dari PowerDelays.Shutdown, bukan dituliskan
    /// sebagai angka di sini. Panel menampilkan angka yang sama kepada operator,
    /// dan dua tempat yang menuliskannya sendiri-sendiri akan berselisih pada
    /// perubahan pertama — dengan dialog yang menjanjikan lima belas detik untuk
    /// perintah yang sebenarnya memberi tiga puluh.
    /// </summary>
    public int DelaySeconds { get; init; } = PowerDelays.Shutdown;
    public bool Force { get; init; }
    public string Message { get; init; } = "Komputer akan dimatikan oleh Admin.";
}

public sealed record RestartCommand : AgentCommand
{
    public int DelaySeconds { get; init; } = PowerDelays.Restart;
    public string Message { get; init; } = "Komputer akan dinyalakan ulang oleh Admin.";
}

/// <summary>
/// Membatalkan shutdown atau restart yang masih dalam hitung mundur.
///
/// Bukan kemewahan: perintah mematikan yang terlanjur dikirim ke 40 unit
/// di tengah praktikum harus bisa ditarik kembali. Inilah alasan perintah
/// daya selalu memakai jeda, bukan dieksekusi seketika.
/// </summary>
public sealed record AbortShutdownCommand : AgentCommand;

/// <summary>
/// Menyuruh agent bersiap menerima stream. Byte video TIDAK lewat kanal ini —
/// ia mengalir langsung lewat UDP dari unit sumber ke klien.
/// </summary>
/// <summary>
/// Meminta sebuah unit MENYIARKAN layarnya sendiri ke seluruh kelas.
///
/// ARAHNYA TERBALIK DARI SIARAN BIASA, dan itu intinya.
///
/// Biasanya panel yang menangkap layar lalu mengirimkannya. Di sini unit yang
/// menangkap, mengirim satu aliran unicast ke relay Superadmin, dan Superadmin
/// yang memancarkannya ke unit lain. Jalur relaynya sama persis — yang berubah
/// hanya siapa yang mengisi ujung pangkalnya.
///
/// Gunanya presentasi mahasiswa. Tanpa ini, satu-satunya cara menampilkan
/// pekerjaan mahasiswa ke kelas adalah memindahkan kabel proyektor ke mejanya —
/// yang justru pekerjaan yang ingin dihapus aplikasi ini.
/// </summary>
public sealed record StartPresentingCommand : AgentCommand
{
    /// <summary>Alamat relay Superadmin. Unit mengirim unicast ke sini.</summary>
    public required string RelayHost { get; init; }
    public required int RelayPort { get; init; }

    public int Width { get; init; } = 1600;
    public int FrameRate { get; init; } = 15;

    /// <summary>
    /// Kualitas JPEG. Lebih rendah daripada siaran dosen secara sengaja.
    ///
    /// Presentasi mahasiswa hampir selalu berupa kode atau slide — teks di
    /// bidang polos, yang tetap terbaca pada kualitas rendah. Sementara itu
    /// PC mahasiswa lebih lemah daripada PC dosen dan sering di Wi-Fi.
    /// </summary>
    public int Quality { get; init; } = 78;
}

/// <summary>Menghentikan penyiaran layar oleh unit.</summary>
public sealed record StopPresentingCommand : AgentCommand;

/// <summary>
/// Memulai pemantauan ujian di unit.
///
/// Judul sesi ikut dikirim supaya klien dapat MENAMPILKANNYA kepada mahasiswa.
/// Pengawasan yang tidak diberitahukan bukan hanya persoalan etika di ruang
/// kelas — ia juga tidak bekerja sebagai pencegah, karena yang tidak diketahui
/// tidak menahan siapa pun. Papan yang menyebutkan "Kuis Minggu 5 · aktivitas
/// dicatat" mencegah lebih banyak daripada catatan yang dibaca setelah ujian
/// selesai.
/// </summary>
public sealed record BeginExamCommand : AgentCommand
{
    public required long SessionId { get; init; }
    public required string Title { get; init; }
}

public sealed record EndExamCommand : AgentCommand;

public sealed record StartBroadcastCommand : AgentCommand
{
    /// <summary>Grup multicast yang harus diikuti klien.</summary>
    public required string MulticastGroup { get; init; }
    public required int Port { get; init; }

    /// <summary>Ditampilkan di jendela penonton agar mahasiswa tahu sumbernya.</summary>
    public string SourceLabel { get; init; } = "Admin";

    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRate { get; init; } = 15;

    /// <summary>
    /// Unit yang harus membuka siaran LANGSUNG DALAM LAYAR PENUH.
    ///
    /// Daftar, bukan satu bendera, karena satu perintah dikirim ke banyak unit
    /// sekaligus sedangkan yang boleh mengambil alih layarnya hanya sebagian —
    /// biasanya satu PC yang tercolok proyektor. Agent memeriksa apakah nama
    /// unitnya sendiri ada di daftar ini.
    ///
    /// Menjadikannya satu bendera untuk semua berarti empat puluh layar
    /// mahasiswa direbut siaran tanpa diminta, dan tidak ada satu pun dari
    /// mereka yang dapat menutupnya.
    /// </summary>
    public string[] FullscreenUnits { get; init; } = [];
}

public sealed record StopBroadcastCommand : AgentCommand;

public sealed record FreezeBroadcastCommand : AgentCommand
{
    /// <summary>True membekukan frame terakhir; false melanjutkan siaran.</summary>
    public bool Frozen { get; init; } = true;
}

public sealed record BeginRemoteCommand : AgentCommand
{
    public int FrameRate { get; init; } = 15;
    /// <summary>False berarti hanya melihat tanpa mengambil alih input.</summary>
    public bool AllowInput { get; init; }
}

public sealed record EndRemoteCommand : AgentCommand;

public sealed record InjectInputCommand : AgentCommand
{
    public required InputEventKind Kind { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int KeyCode { get; init; }
    public int WheelDelta { get; init; }

    /// <summary>Tombol mouse: 0 kiri, 1 tengah, 2 kanan.</summary>
    public int Button { get; init; }
}

public enum InputEventKind
{
    MouseMove, MouseDown, MouseUp, MouseWheel,
    KeyDown, KeyUp, SecureAttentionSequence,
}

public sealed record SendChatCommand : AgentCommand
{
    public required string Text { get; init; }
    public required string FromDisplayName { get; init; }

    /// <summary>
    /// True bila pesan disebar ke seluruh kelas.
    ///
    /// Klien menandainya berbeda supaya mahasiswa tidak mengira pengumuman
    /// kelas sebagai teguran pribadi — perbedaan yang kecil di kode tetapi
    /// besar bagi orang yang menerimanya di depan teman-temannya.
    /// </summary>
    public bool ToEveryone { get; init; }
}

/// <summary>Admin sudah menangani permintaan bantuan; penandanya dipadamkan.</summary>
public sealed record AcknowledgeHelpCommand : AgentCommand;

/// <summary>
/// Mengosongkan percakapan menjelang kelas berikutnya.
///
/// Perintah ini HARUS sampai ke klien, bukan cukup menghapus di konsol. Layar
/// mahasiswa berikutnya adalah PC fisik yang sama dengan mahasiswa sebelumnya;
/// menghapus hanya di sisi konsol berarti percakapan lama tetap terpampang di
/// tempat yang justru paling mudah terbaca orang yang tidak berhak.
/// </summary>
public sealed record ClearChatCommand : AgentCommand;

/// <summary>
/// Menyuruh agent MENGUNDUH sebuah berkas, bukan menerima kirimannya.
///
/// Perintah ini hanya membawa alamat dan sidik jari; isinya ditarik agent lewat
/// HTTP dari Superadmin. Dengan begitu unit yang lambat tidak menahan yang lain,
/// unduhan yang putus dapat dilanjutkan, dan panel tidak perlu melayani 40
/// aliran serentak sambil tetap menangkap layar untuk siaran.
/// </summary>
public sealed record PushFileCommand : AgentCommand
{
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>
    /// PATH unduhan, bukan URL lengkap — misalnya "/files/a1b2c3".
    ///
    /// Superadmin sengaja tidak menyusun alamat lengkapnya. Mesin dengan
    /// beberapa kartu jaringan punya beberapa alamat, dan hanya agent yang tahu
    /// lewat mana ia benar-benar terhubung. Agent yang menambahkan host dan
    /// port di depan path ini, memakai endpoint yang sedang ia pakai.
    /// </summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Sidik SHA-256 isi berkas, diverifikasi agent setelah unduhan.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// Menyuruh agent memperbarui dirinya ke versi baru.
/// Agent mengunduh installer baru lalu memasangnya secara senyap di latar belakang.
/// </summary>
public sealed record UpdateLabCommand : AgentCommand
{
    public required string TargetVersion { get; init; }
    public required string DownloadUrl { get; init; }
    public string? Sha256 { get; init; }
}
