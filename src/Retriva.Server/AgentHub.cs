using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Retriva.Server.Data;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Titik masuk seluruh komunikasi dari agent. Nama metode di sini harus sama
/// persis dengan yang dipanggil agent lewat InvokeAsync/SendAsync.
/// </summary>
public sealed class AgentHub(
    UnitRegistry registry,
    UnitStore units,
    ExamStore exams,
    ActivityLog activity,
    ChatStore chats,
    BatchTracker batches,
    ConsoleConfig config,
    LabNetwork lab,
    ILogger<AgentHub> log) : Hub
{
    /// <summary>
    /// Nomor unit menurut alamat IP sambungan dan konfigurasi lab.
    ///
    /// Mengembalikan null bila alamatnya di luar rentang yang dikonfigurasi —
    /// laptop tamu, mesin di subnet lain, atau lab yang memang tidak memakai
    /// alamat statis. Dalam hal itu nama usulan agent yang dipakai, dan asisten
    /// dapat memberinya alias.
    /// </summary>
    /// <summary>
    /// Setelan akses yang sedang berlaku, dibaca dari konfigurasi yang hidup.
    ///
    /// Statik supaya PanelHub dapat memakai sumber yang sama saat mendorong
    /// perubahan. Dua tempat yang menyusun objek ini sendiri-sendiri akan
    /// berbeda begitu satu medan ditambahkan dan yang lain lupa diperbarui —
    /// dan bedanya muncul sebagai unit yang mematuhi aturan lama tanpa ada yang
    /// tahu mengapa.
    /// </summary>
    public static AccessPolicy CurrentPolicy(ConsoleConfig config) => new()
    {
        RequireStudentLogin = config.RequireStudentLogin,
        AllowGuestAccess = config.AllowGuestAccess,
        StudentsMayRejectControl = config.StudentsMayRejectControl,
    };

    private string? ProposedUnitId(IPAddress? remote)
    {
        if (remote is null) return null;

        var address = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();

        // Hanya alamat di dalam subnet lab yang dinomori. Tanpa syarat ini,
        // laptop dosen di 10.x akan mendapat nomor unit seolah ia PC mahasiswa.
        if (!address.StartsWith(config.SubnetPrefix, StringComparison.Ordinal)) return null;

        var lastDot = address.LastIndexOf('.');
        if (lastDot < 0 || !int.TryParse(address[(lastDot + 1)..], out var octet)) return null;

        // Tabel, bukan rumus. Oktet yang belum dipetakan mengembalikan null,
        // dan unitnya masuk memakai nama mesin Windows-nya — terlihat jelas di
        // layar Konfigurasi sebagai baris yang belum dinamai, bukan hilang.
        return config.UnitNameForOctet(octet);
    }

    public Task<RegisterResponse> Register(RegisterRequest request)
    {
        /*
            NAMA UNIT DIHITUNG KOORDINATOR, BUKAN DITENTUKAN AGENT.

            Sebelumnya agent menurunkannya sendiri dari alamat IP-nya, memakai
            `new ConsoleConfig()` — sebuah konfigurasi BARU dengan nilai bawaan
            192.168.1. dan rentang 1..40. Agent tidak punya console.json milik
            koordinator dan tidak pernah menanyakannya, sehingga apa pun yang
            diatur asisten di layar Konfigurasi tidak berpengaruh pada satu unit
            pun. Di lab beralamat 192.168.0.x, penurunannya selalu gagal dan
            seluruh unit bernama seperti nama mesin Windows-nya.

            Alamat yang dipakai di sini berasal dari KONEKSI, bukan dari isi
            pesan. Agent di belakang NAT atau yang salah membaca kartu
            jaringannya sendiri akan melaporkan alamat yang bukan alamatnya.
        */
        var remote = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        var proposed = ProposedUnitId(remote);

        var (result, token, unitId) = units.Validate(
            request, config.AgentEnrollmentSecret, proposed);

        if (result != EnrollmentResult.Accepted)
        {
            var reason = result switch
            {
                EnrollmentResult.BadSecret     => "Kode pendaftaran salah",
                EnrollmentResult.TokenMismatch => "Token perangkat tidak dikenali",
                // Tidak lagi dikembalikan Validate — MAC yang berubah kini
                // dicatat, bukan menolak. Cabang ini tinggal supaya switch tetap
                // menyeluruh bila suatu saat nilainya dipakai lagi.
                EnrollmentResult.MacMismatch   => "MAC address tidak cocok dengan catatan unit ini",
                EnrollmentResult.Blocked       => "Unit ini diblokir oleh asisten",
                _                              => "Ditolak",
            };

            log.LogWarning("Pendaftaran {UnitId} dari {Ip} ditolak: {Reason}",
                unitId, request.IpAddress, reason);
            activity.Write("system", "enrollment.rejected", request.UnitId, reason);

            return Task.FromResult(new RegisterResponse { Accepted = false, RejectReason = reason });
        }

        // Registri memakai nama yang sah, bukan yang diusulkan agent. Kalau
        // tidak, grid menampilkan satu unit dengan dua nama: yang dari registri
        // dan yang dari basis data.
        // Alamat lokal koneksi ini adalah alamat koordinator yang TERBUKTI
        // terjangkau dari unit tersebut — bukan tebakan, melainkan alamat yang
        // baru saja dipakainya untuk sampai ke sini. Dipakai nanti untuk
        // menyuruhnya mengirim layar ke relay.
        var seenAs = Context.GetHttpContext()?.Connection.LocalIpAddress;
        var coordinatorHost = seenAs is null || IPAddress.IsLoopback(seenAs)
            ? null
            : (seenAs.IsIPv4MappedToIPv6 ? seenAs.MapToIPv4() : seenAs).ToString();

        var unit = registry.Upsert(
            request with { UnitId = unitId }, Context.ConnectionId, coordinatorHost);

        // Antarmuka tempat agent ini menjangkau koordinator dicatat. Itulah
        // antarmuka yang dipakai relay untuk memancarkan siaran — lihat
        // LabNetwork untuk alasan panjangnya.
        lab.ObserveAgent(Context.GetHttpContext()?.Connection.LocalIpAddress, unitId);
        log.LogInformation("Unit {UnitId} terdaftar dari {Ip} ({Host})",
            unit.UnitId, unit.IpAddress, unit.HostName);
        activity.Write("system", "unit.registered", unit.UnitId, $"{unit.IpAddress} · {unit.HostName}");

        /*
            Unit yang mendaftar SELAGI sesi ujian berjalan langsung disuruh
            memantau.

            Tanpa ini, PC yang dinyalakan setelah ujian dimulai — mahasiswa yang
            terlambat, atau PC yang sempat mati — tidak akan dipantau sama sekali.
            Kegagalannya tidak muncul sebagai galat: unit itu tampil online,
            menjalankan setiap perintah dengan benar, dan lini masanya kosong.
            Dan kosong terbaca seperti bersih.

            Dikirim tanpa menunggu, setelah jawaban pendaftaran disusun. Menunggu
            di sini berarti pendaftaran unit tertahan oleh pengiriman perintah,
            dan unit yang gagal menerimanya akan gagal mendaftar juga.
        */
        if (exams.Active() is { } running)
        {
            _ = Clients.Caller.SendAsync("Execute", new BeginExamCommand
            {
                SessionId = running.Id,
                Title = running.Title,
                IssuedBy = running.StartedBy,
            });
            log.LogInformation("Unit {Unit} menyusul sesi ujian {Title}", unit.UnitId, running.Title);
        }

        return Task.FromResult(new RegisterResponse
        {
            Accepted = true,
            ConfirmedUnitId = unit.UnitId,
            IssuedDeviceToken = token,
            HeartbeatSeconds = RetrivaProtocol.HeartbeatSeconds,
            ThumbnailSeconds = 2,
            ThumbnailWidth = 480,
            Policy = CurrentPolicy(config),
        });
    }

    /// <summary>
    /// Menerima satu pengamatan ujian dari sebuah unit.
    ///
    /// UnitId DITIMPA dari koneksi, tidak dipakai apa adanya dari pesan. Nilai
    /// yang dikirim agent tidak dibuktikan oleh apa pun, dan catatan pengawasan
    /// yang dapat ditulis atas nama unit lain tidak berguna sebagai catatan.
    /// </summary>
    public Task ReportExamEvent(ExamEvent evt)
    {
        var unitId = registry.Snapshot()
            .FirstOrDefault(u => u.ConnectionId == Context.ConnectionId)?.UnitId;

        if (unitId is null)
        {
            log.LogWarning("Pengamatan ujian dari koneksi yang tidak dikenali, dibuang");
            return Task.CompletedTask;
        }

        // False berarti tidak ada sesi berjalan. Dicatat, bukan didiamkan:
        // pengamatan yang tiba setelah sesi ditutup wajar terjadi sesaat, tetapi
        // aliran yang terus-menerus berarti ada klien yang tidak berhenti
        // memantau — dan itu justru kegagalan yang paling perlu terlihat.
        if (!exams.Record(evt with { UnitId = unitId }))
            log.LogWarning("Pengamatan ujian dari {Unit} dibuang: tidak ada sesi berjalan", unitId);

        return Task.CompletedTask;
    }

    public Task Heartbeat(HeartbeatReport report)
    {
        registry.ApplyHeartbeat(report);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pratinjau berkala dari agent. Sengaja JPEG kecil, bukan video:
    /// admin hanya perlu tahu mahasiswa sedang membuka apa, dan 240x135
    /// tiap dua detik itu sekitar 5 KB per unit — ringan bahkan untuk 40 unit.
    /// </summary>
    public Task PublishThumbnail(ThumbnailFrame frame)
    {
        if (frame.JpegData.Length == 0) return Task.CompletedTask;

        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(frame.JpegData);
        if (registry.StoreThumbnail(frame.UnitId, dataUrl))
            ThumbnailArrived?.Invoke(frame.UnitId, dataUrl);

        return Task.CompletedTask;
    }

    /// <summary>Dipakai jendela utama untuk meneruskan pratinjau ke UI.</summary>
    public static event Action<string, string>? ThumbnailArrived;

    /// <summary>
    /// Pesan dari mahasiswa. Disimpan lebih dulu, baru diumumkan ke UI.
    ///
    /// Urutannya disengaja: kalau panel admin ditutup tepat setelah pesan tiba,
    /// pesannya tetap ada di basis data. Diumumkan dulu lalu disimpan, pesan
    /// yang sempat terlihat sesaat bisa hilang selamanya.
    /// </summary>
    public Task PublishChat(ChatMessage message)
    {
        chats.Append(message);
        ChatArrived?.Invoke(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nama unit yang diketik mahasiswa, diteruskan agent.
    ///
    /// Unit id diambil dari SAMBUNGAN, tidak dipercaya dari isi pesan. Agent
    /// mengirimkannya juga, tetapi memakainya berarti unit mana pun yang
    /// tersambung dapat menamai unit mana pun yang lain — termasuk menimpa nama
    /// unit tetangganya dengan sesuatu yang menyesatkan, dari mesin yang tidak
    /// akan pernah dicurigai karena namanya sendiri tetap wajar.
    /// </summary>
    public Task SetUnitSelfName(string unitId, string name)
    {
        var caller = registry.Snapshot()
            .FirstOrDefault(u => u.ConnectionId == Context.ConnectionId)?.UnitId;

        if (caller is null)
        {
            log.LogWarning("Nama unit dari koneksi yang tidak dikenali, dibuang");
            return Task.CompletedTask;
        }

        if (!units.SetSelfName(caller, name)) return Task.CompletedTask;

        activity.Write(caller, "unit.selfname", caller,
            string.IsNullOrWhiteSpace(name) ? "nama dihapus mahasiswa" : $"dinamai \"{name}\"");

        // Tidak ada dorongan khusus ke panel. PanelBroadcaster sudah menyusun
        // ulang daftar unit secara berkala dan akan membawanya pada putaran
        // berikutnya — jeda beberapa detik untuk penamaan yang dilakukan sekali
        // di awal praktikum tidak sepadan dengan jalur dorongan tersendiri.
        return Task.CompletedTask;
    }

    public Task RaiseHand(HelpRequest request)
    {
        if (!registry.SetHelpRequested(request.UnitId, request.Raised)) return Task.CompletedTask;

        activity.Write(request.UnitId, request.Raised ? "help.raised" : "help.cancelled",
            request.UnitId, request.DisplayName);
        HelpChanged?.Invoke(request);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Kemajuan unduhan dari satu unit.
    ///
    /// Tidak disimpan ke basis data setiap kali. Laporan datang berkali-kali per
    /// detik per unit selama pembagian berlangsung; menulis tiap satu berarti
    /// ribuan baris untuk satu berkas, dan tabel file_transfers hanya perlu
    /// mencatat hasil akhirnya.
    /// </summary>
    public Task ReportFileProgress(FileTransferProgress progress)
    {
        FileProgressArrived?.Invoke(progress);

        if (progress.Status is "selesai" or "gagal")
        {
            activity.Write(progress.UnitId,
                progress.Status == "selesai" ? "file.received" : "file.failed",
                progress.UnitId, $"{progress.FileName} — {progress.Detail}");
        }

        return Task.CompletedTask;
    }

    public static event Action<FileTransferProgress>? FileProgressArrived;

    /// <summary>
    /// Pesan chat baru, dari arah mana pun.
    ///
    /// Namanya menyebut AgentHub karena di sinilah pesan mahasiswa masuk, tetapi
    /// pesan admin juga diedarkan lewat sini — lihat PanelHub.SendChat. Satu
    /// jalur untuk kedua arah adalah satu-satunya cara memastikan seluruh panel
    /// melihat percakapan yang sama.
    /// </summary>
    public static event Action<ChatMessage>? ChatArrived;

    /// <summary>Memancarkan pesan yang tidak berasal dari agent.</summary>
    internal static void RaiseChatArrived(ChatMessage message) => ChatArrived?.Invoke(message);
    public static event Action<HelpRequest>? HelpChanged;
    internal static void RaiseHelpChanged(HelpRequest request) => HelpChanged?.Invoke(request);

    public Task ReportCommandResult(CommandResult result)
    {
        log.LogInformation("Hasil {CommandId} dari {UnitId}: {Outcome} — {Detail}",
            result.CommandId, result.UnitId, result.Outcome, result.Detail);

        // Balasan yang termasuk perintah massal dikelompokkan, bukan dicatat
        // satu per satu — ringkasannya yang ditulis saat batch selesai.
        var partOfBatch = batches.Record(result);

        // Hanya hasil tunggal yang tidak biasa yang dicatat. Empat puluh unit
        // membalas setiap perintah massal; menulis semuanya akan menenggelamkan
        // jejak audit sampai tidak berguna.
        if (!partOfBatch && result.Outcome != CommandOutcome.Success)
        {
            activity.Write("agent", "command.failed", result.UnitId,
                $"{result.Outcome} — {result.Detail}");
        }

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.MarkDisconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
