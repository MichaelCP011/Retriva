// System.Net.Http ikut implicit usings pada SDK konsol, tetapi tidak pada SDK
// desktop yang dipakai project ini — karena itu HttpClientHandler perlu disebut
// di sini meski berkas serupa di Retriva.Agent tidak memerlukannya.
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Retriva.Server;
using Retriva.Shared;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;
using Retriva.Shared.Security;

namespace Retriva.Admin;

/// <summary>
/// Sambungan panel ke Superadmin.
///
/// SELALU LEWAT SIGNALR, TERMASUK KETIKA KEDUANYA DI MESIN YANG SAMA.
///
/// Terasa berputar — PC Asisten memanggil dirinya sendiri lewat 127.0.0.1
/// padahal objeknya ada di dalam proses yang sama. Tetapi jalan pintas
/// dalam-proses akan menjadi jalur yang paling sering dilalui saat
/// mengembangkan, sekaligus jalur yang paling jarang dijalankan di lab.
/// Panel dosen akan memakai cabang yang hampir tidak pernah dicoba, dan
/// kesalahannya baru muncul di depan satu kelas.
///
/// Satu jalur untuk semua panel berarti setiap kali saya menjalankan Retriva
/// di mesin sendiri, saya menjalankan jalur yang sama persis dengan yang
/// dipakai dosen.
/// </summary>
public sealed class SuperadminLink : IAsyncDisposable
{
    /// <summary>
    /// Sambungan yang sedang berjalan, null selama panel belum tertaut.
    ///
    /// Dapat diganti sepanjang hidup objek ini. URL sebuah HubConnection
    /// ditetapkan saat dibangun, jadi menyambung ke lab lain berarti membangun
    /// yang baru — tetapi SuperadminLink-nya tetap objek yang sama, sehingga
    /// jendela tidak perlu memindahkan langganan peristiwanya. Kalau objeknya
    /// yang diganti, setiap penautan ulang menuntut jendela berlangganan ulang,
    /// dan satu langganan yang terlewat menghasilkan panel yang tersambung
    /// tetapi diam selamanya.
    /// </summary>
    private HubConnection? _hub;

    private readonly PanelConfig _panel;
    private readonly ILogger _log;

    /// <summary>
    /// Kode pendaftaran yang baru diketik pengguna. Null pada sambungan
    /// berikutnya, karena sejak itu token perangkat yang membuktikan identitas.
    /// </summary>
    private string? _enrollmentCode;

    /// <summary>Peran yang diminta pada penautan ini.</summary>
    private PanelRole _requestedRole = PanelRole.Dosen;

    /// <summary>Alamat yang sedang dicoba, dicatat ke daftar lab bila diterima.</summary>
    private string _currentHost = "";
    private int _currentPort = 8443;

    /// <summary>
    /// Sidik jari sertifikat yang benar-benar dilihat pada sambungan ini.
    ///
    /// Direkam oleh callback validasi TLS, lalu dipakai dua kali: untuk
    /// menghitung bukti yang dikirim, dan untuk memeriksa bukti balik. Kalau
    /// yang dipakai adalah nilai dari konfigurasi, ikatannya menjadi omong
    /// kosong — bukti akan selalu cocok dengan dirinya sendiri, apa pun
    /// sertifikat yang sebenarnya menjawab.
    /// </summary>
    private volatile string? _observedThumbprint;

    public PanelRole Role { get; private set; } = PanelRole.Pending;
    public PanelRights Rights { get; private set; } = PanelRights.None;
    public string LabName { get; private set; } = "Laboratorium";

    /// <summary>Alasan penolakan terakhir, null bila sambungannya diterima.</summary>
    public string? RejectReason { get; private set; }

    public bool Connected => _hub is { State: HubConnectionState.Connected };
    public bool Ready => Connected && Role != PanelRole.Pending;

    /// <summary>
    /// Panel ini sudah pernah ditautkan ke sebuah lab.
    ///
    /// Berbeda dari Connected: laptop dosen yang dibawa pulang tetap tertaut,
    /// hanya sedang tidak tersambung. Membedakan keduanya menentukan apa yang
    /// ditawarkan layar — "coba sambungkan lagi" atau "masukkan kode
    /// pendaftaran" — dan menawarkan yang kedua kepada panel yang sebenarnya
    /// cuma di luar jangkauan akan membuat orang mendaftar ulang tanpa perlu.
    /// </summary>
    public bool Linked => _panel.IsEnrolled;

    /// <summary>Lab yang sedang atau terakhir dipakai.</summary>
    public KnownLab? CurrentLab { get; private set; }

    /// <summary>Seluruh lab yang mengenali panel ini.</summary>
    public IReadOnlyList<KnownLab> KnownLabs => _panel.Labs;

    /// <summary>
    /// Enam karakter pertama id perangkat, untuk dicocokkan dengan mata.
    ///
    /// MENGGANTIKAN KODE PENDAFTARAN, DENGAN ARAH YANG TERBALIK.
    ///
    /// Kode lama mengalir dari asisten ke dosen dan harus diketik. Kode ini
    /// hanya dibaca: panel dosen menampilkannya, asisten melihat nilai yang
    /// sama di baris permintaannya, lalu menyetujui baris yang benar.
    ///
    /// Gunanya bukan menahan penyusup — persetujuan yang menahannya. Gunanya
    /// adalah memastikan asisten menyetujui panel yang dimaksudnya ketika dua
    /// permintaan datang dalam menit yang sama, yang di kelas pagi bukan hal
    /// langka.
    /// </summary>
    public string DeviceCode => _panel.DeviceId.Length >= 6
        ? _panel.DeviceId[..6].ToUpperInvariant()
        : _panel.DeviceId.ToUpperInvariant();

    public string CoordinatorHost => CurrentLab?.Host ?? _panel.MostRecentLab?.Host ?? "";
    public int CoordinatorPort => CurrentLab?.Port ?? _panel.MostRecentLab?.Port ?? 8443;
    public string CoordinatorThumbprint => CurrentLab?.Thumbprint ?? "";

    /// <summary>Dipicu setiap peran atau keadaan sambungan berubah.</summary>
    public event Action? StatusChanged;

    /* dorongan dari Superadmin */
    public event Action<UnitSnapshot[]>? UnitsChanged;
    public event Action<ActivityEntryDto[]>? ActivityChanged;
    public event Action<ChatMessage>? ChatArrived;
    public event Action<BroadcastState>? BroadcastChanged;
    public event Action<BatchSummary>? BatchCompleted;
    public event Action<FileTransferProgress>? FileProgress;
    public event Action<string, string>? ThumbnailArrived;
    public event Action<HelpRequest>? HelpChanged;
    public event Action<PanelSummary[]>? PanelsChanged;

    /// <summary>
    /// Daftar unit terakhir yang diterima.
    ///
    /// Disimpan karena beberapa penangan UI berjalan sinkron dan butuh
    /// menjawab pertanyaan "unit mana yang online" tanpa menunggu perjalanan
    /// ke Superadmin. Isinya boleh tertinggal paling lama setengah detik —
    /// cukup baru untuk memilih tujuan, dan Superadmin tetap memeriksa ulang
    /// sebelum mengirim apa pun, jadi daftar yang basi tidak berakibat
    /// perintah mendarat di unit yang salah.
    /// </summary>
    public IReadOnlyList<UnitSnapshot> Units { get; private set; } = [];

    public SuperadminLink(PanelConfig panel, ILogger log)
    {
        _log = log;
        _panel = panel;
    }

    /// <summary>
    /// Menyambung ke sebuah lab, atau menyambung ulang ke lab lain.
    ///
    /// <paramref name="enrollmentCode"/> hanya diisi saat menautkan pertama
    /// kali. Sesudah tertaut, token perangkat yang membuktikan identitas dan
    /// kodenya tidak dipakai lagi — kode yang terus dikirim adalah kode yang
    /// terus punya kesempatan bocor.
    /// </summary>
    public async Task ConnectAsync(string host, int port,
                                   string? enrollmentCode = null,
                                   PanelRole requestedRole = PanelRole.Dosen)
    {
        // Sambungan lama dibongkar lebih dulu. Dibiarkan hidup, ia akan terus
        // mendorong keadaan dari lab sebelumnya ke jendela yang sama — dua
        // sumber yang saling menimpa, dan yang menang bergantung urutan tiba.
        await TeardownAsync();

        _enrollmentCode = enrollmentCode;
        _requestedRole = requestedRole;
        _currentHost = host;
        _currentPort = port;
        _observedThumbprint = null;
        RejectReason = null;

        var url = $"https://{host}:{port}{PanelProtocol.HubPath}";

        var hub = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                // Dua callback, bukan satu. Negosiasi awal berjalan lewat HTTP
                // biasa, lalu transport naik ke WebSocket yang punya jalur
                // validasi sertifikatnya sendiri. Menyetel salah satunya saja
                // membuat penyematan bocor tanpa satu pun tanda di layar.
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http)
                    {
                        http.ServerCertificateCustomValidationCallback =
                            (_, certificate, _, _) => AcceptCertificate(certificate);
                    }
                    return handler;
                };

                options.WebSocketConfiguration = socket =>
                {
                    socket.RemoteCertificateValidationCallback =
                        (_, certificate, _, _) => AcceptCertificate(certificate);
                };
            })
            .WithAutomaticReconnect()
            .Build();

        hub.On<UnitSnapshot[]>("Units", u =>
        {
            Units = u;
            UnitsChanged?.Invoke(u);
        });
        hub.On<ActivityEntryDto[]>("Activity", a => ActivityChanged?.Invoke(a));
        hub.On<ChatMessage>("Chat", m => ChatArrived?.Invoke(m));
        hub.On<BroadcastState>("Broadcast", b => BroadcastChanged?.Invoke(b));
        hub.On<BatchSummary>("Batch", s => BatchCompleted?.Invoke(s));
        hub.On<string, string>("Thumbnail", (unitId, dataUrl) => ThumbnailArrived?.Invoke(unitId, dataUrl));
        hub.On<HelpRequest>("Help", r => HelpChanged?.Invoke(r));
        hub.On<PanelSummary[]>("Panels", p => PanelsChanged?.Invoke(p));

        // Peran berubah tanpa perlu menyambung ulang.
        //
        // Persetujuan terjadi setelah panel tersambung, jadi jawaban
        // RegisterPanel sudah lewat. Tanpa jalur ini dosen tetap melihat
        // "menunggu persetujuan" meski asisten sudah menekan tombolnya —
        // sampai aplikasinya ditutup dan dibuka lagi.
        /*
            Sesi ujian dikabarkan ke SELURUH panel, bukan hanya yang memulainya.

            Dosen dan asisten sama-sama punya hak ExamMode. Tanpa kabar ini,
            asisten yang membuka panelnya setelah dosen memulai sesi akan melihat
            tombol "Mulai sesi" — lalu menekannya, dan penolakannya harus
            dijelaskan oleh pesan galat. Yang lebih buruk: ia dapat menekan
            "Akhiri" tanpa tahu sesi siapa yang sedang berjalan.
        */
        hub.On<ExamSession?>("ExamState", session =>
        {
            Exam = session;
            ExamChanged?.Invoke();
        });

        hub.On<PanelWelcome>("Role", w =>
        {
            Role = w.Role;
            Rights = w.Rights;
            LabName = w.LabName;
            RejectReason = null;
            _log.LogInformation("Peran panel diperbarui menjadi {Role}", w.Role);
            StatusChanged?.Invoke();
        });
        hub.On<FileTransferProgress>("FileProgress", p => FileProgress?.Invoke(p));

        // Mendaftar ulang setiap kali sambungan pulih.
        //
        // Superadmin mengenali panel lewat connection id, dan connection id
        // berganti pada setiap sambungan baru. Tanpa pendaftaran ulang, panel
        // terlihat tersambung tetapi setiap perintahnya ditolak dengan alasan
        // "belum terdaftar" — kegagalan yang menyamar sebagai masalah wewenang.
        hub.Reconnected += async _ =>
        {
            await RegisterAsync();
            StatusChanged?.Invoke();
        };

        hub.Closed += _ =>
        {
            Role = PanelRole.Pending;
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        };

        _hub = hub;

        await hub.StartAsync();
        await RegisterAsync();

        StatusChanged?.Invoke();
    }

    private async Task TeardownAsync()
    {
        if (_hub is not { } old) return;

        _hub = null;
        Role = PanelRole.Pending;
        Rights = PanelRights.None;
        Units = [];

        try
        {
            await old.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Sambungan yang sudah mati kadang melempar saat dibuang. Tidak ada
            // yang perlu diperbaiki — tetapi tetap dicatat, karena "kadang"
            // yang tidak pernah terlihat lama-lama menjadi "selalu".
            _log.LogDebug(ex, "Pengecualian saat membongkar sambungan lama");
        }
    }

    /// <summary>
    /// Melupakan SATU lab, bukan semuanya.
    ///
    /// Dulu ini membuang satu-satunya tautan yang ada, karena panel memang
    /// hanya dapat mengingat satu. Sekarang tiap lab berdiri sendiri: dosen
    /// yang melupakan lab lama tetap dapat masuk ke lab lain tanpa mendaftar
    /// ulang di sana.
    ///
    /// Perlu terutama bila PC Asisten sebuah lab diinstal ulang — sertifikatnya
    /// berganti, sehingga lab itu terlihat sebagai lab baru dan barisnya yang
    /// lama menggantung tanpa guna.
    /// </summary>
    public async Task ForgetLabAsync(string? thumbprint)
    {
        var target = thumbprint ?? CurrentLab?.Thumbprint;
        if (string.IsNullOrWhiteSpace(target)) return;

        // Sambungan diputus hanya bila yang dilupakan memang yang sedang dipakai.
        // Memutus tanpa syarat akan menjatuhkan sesi yang sedang berjalan hanya
        // karena seseorang merapikan daftar lab lamanya.
        if (string.Equals(CurrentLab?.Thumbprint, target, StringComparison.OrdinalIgnoreCase))
        {
            await TeardownAsync();
            CurrentLab = null;
            LabName = "Laboratorium";
        }

        _panel.Forget(target);
        RejectReason = null;

        ConfigStore.Save(RetrivaPaths.PanelIdentityFile, _panel);
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Menyambung ke lab yang paling cocok dari yang terdengar di jaringan.
    ///
    /// Dipanggil saat aplikasi dibuka dan setiap kali daftar lab berubah.
    /// Dosen yang berjalan dari lab A ke lab B tidak menekan apa pun: panelnya
    /// mendengar beacon lab B, mengenali sidik jarinya, dan menyambung sendiri.
    /// </summary>
    public async Task<bool> TryAutoConnectAsync(IEnumerable<(string Thumbprint, string Host, int Port)> heard)
    {
        if (Connected) return true;

        // Yang paling terakhir dipakai dicoba lebih dulu. Di gedung dengan dua
        // lab yang terdengar bersamaan, itu tebakan terbaik yang tersedia — dan
        // kalau salah, dosen tinggal memilih yang lain sekali.
        var candidates = heard
            .Select(h => (h, lab: _panel.FindByThumbprint(h.Thumbprint)))
            .Where(x => x.lab is not null)
            .OrderByDescending(x => x.lab!.LastUsed);

        foreach (var (h, _) in candidates)
        {
            try
            {
                await ConnectAsync(h.Host, h.Port);
                if (Connected) return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Gagal menyambung otomatis ke {Host}", h.Host);
            }
        }

        return false;
    }

    /// <summary>
    /// Menyetel mesin ini menjadi Superadmin pada penyalaan berikutnya.
    ///
    /// Berlaku setelah aplikasi dibuka ulang, bukan seketika. Menyalakan
    /// Kestrel, membuat sertifikat, dan membuka basis data di tengah jalan
    /// berarti separuh aplikasi masih memegang keadaan dari peran sebelumnya —
    /// dan yang mana persisnya bergantung pada urutan yang sulit ditebak.
    /// Memulai ulang membuat keadaannya satu, bukan campuran.
    /// </summary>
    public void PromoteToSuperadmin()
    {
        _panel.IsSuperadmin = true;
        _panel.Configured = true;
        ConfigStore.Save(RetrivaPaths.PanelIdentityFile, _panel);
    }

    /// <summary>Mesin ini disetel sebagai penyelenggara lab.</summary>
    public bool IsSuperadminMachine => _panel.IsSuperadmin;

    /// <summary>
    /// Menentukan apakah sertifikat yang menjawab boleh diterima.
    ///
    /// DUA KEADAAN YANG BERBEDA TAJAM.
    ///
    /// Panel yang sudah terdaftar punya sidik jari tersimpan, dan hanya
    /// sertifikat itu yang diterima. Tidak ada jalan mundur ke penerimaan
    /// bebas — sekali tersemat, seterusnya tersemat. Kalau ada jalan mundur,
    /// penyusup cukup membuat pendaftaran seolah gagal untuk memicunya.
    ///
    /// Panel yang belum terdaftar belum tahu apa yang harus dipercayainya, jadi
    /// sertifikat apa pun dilewatkan DI LAPISAN INI — lalu sidik jarinya
    /// direkam. Penerimaannya belum berarti kepercayaan: yang menentukan adalah
    /// bukti balik yang diperiksa setelah pendaftaran, dan sidik jari ini baru
    /// disimpan bila bukti itu cocok.
    ///
    /// Karena itu tidak ada jendela "terima saja" yang bertahan: sertifikat
    /// asing dapat menyelesaikan jabat tangan TLS, tetapi tidak dapat
    /// menghasilkan bukti balik tanpa mengetahui kode pendaftaran.
    /// </summary>
    private bool AcceptCertificate(System.Security.Cryptography.X509Certificates.X509Certificate? cert)
    {
        if (cert is null) return false;

        var seen = CertificatePin.Thumbprint(cert);
        _observedThumbprint = seen;

        /*
            Sertifikat apa pun dilewatkan DI LAPISAN INI, dan itu bukan
            kelonggaran.

            Yang mengikat kepercayaan bukan penolakan di sini, melainkan
            PENCARIAN TOKEN BERDASARKAN SIDIK JARI beberapa langkah kemudian.
            Koordinator asing menyodorkan sertifikat yang tidak cocok dengan
            satu pun lab yang dikenal, sehingga tidak ada token yang dikirim
            kepadanya — panel masuk sebagai permintaan baru yang harus disetujui
            manusia, dan tidak ada rahasia apa pun yang berpindah.

            Menolak di sini justru merusak kemampuan berpindah lab: panel harus
            sudah tahu sertifikat lab tujuan sebelum pernah menyambung ke sana,
            yang mustahil pada kunjungan pertama ke ruangan mana pun.
        */
        return true;
    }

    private async Task RegisterAsync()
    {
        if (_hub is not { } hub) return;

        var seen = _observedThumbprint;

        if (seen is null)
        {
            RejectReason = "Sertifikat koordinator tidak terbaca.";
            Role = PanelRole.Pending;
            return;
        }

        /*
            TOKEN DICARI BERDASARKAN SIDIK JARI YANG BARU DILIHAT.

            Inilah yang mengikat kepercayaan, menggantikan penolakan sertifikat
            di lapisan TLS. Panel mengirim token HANYA kepada koordinator yang
            sertifikatnya cocok dengan salah satu lab yang pernah menyetujuinya.

            Koordinator asing tidak cocok dengan satu pun, jadi tidak menerima
            token apa pun — panel masuk sebagai permintaan baru yang harus
            disetujui manusia, dan tidak ada rahasia yang berpindah.

            Sekaligus inilah yang membuat dosen dapat berpindah ruangan: masuk
            ke lab lain berarti sidik jari lain, token lain, dan tidak satu pun
            di antaranya menghapus yang lama.
        */
        var known = _panel.FindByThumbprint(seen);

        var hello = new PanelHello
        {
            DeviceId = _panel.DeviceId,
            DisplayName = _panel.DisplayName,
            MachineName = Environment.MachineName,
            AppVersion = typeof(SuperadminLink).Assembly.GetName().Version?.ToString() ?? "0",
            DeviceToken = known?.DeviceToken,
            RequestedRole = _requestedRole,
            WiredConnection = IsWired(),
        };

        var welcome = await hub.InvokeAsync<PanelWelcome>("RegisterPanel", hello);

        if (!welcome.Accepted)
        {
            RejectReason = welcome.RejectReason;
            Role = PanelRole.Pending;
            Rights = PanelRights.None;
            _log.LogWarning("Panel ditolak Superadmin: {Reason}", welcome.RejectReason);
            return;
        }

        RejectReason = null;
        Role = welcome.Role;
        Rights = welcome.Rights;
        LabName = welcome.LabName;

        // Lab dicatat hanya bila panel benar-benar diterima. Mencatat lebih
        // awal berarti daftar lab terisi oleh koordinator yang menolak panel
        // ini, dan panel akan mencoba menyambung ke sana lagi setiap kali
        // dibuka — ditolak lagi, setiap kali.
        CurrentLab = _panel.Remember(
            seen, welcome.LabName, _currentHost, _currentPort, welcome.IssuedDeviceToken);

        ConfigStore.Save(RetrivaPaths.PanelIdentityFile, _panel);

        _log.LogInformation("Panel terdaftar sebagai {Role} di {Lab}", Role, LabName);
    }

    /* ================= perintah ================= */

    /// <summary>
    /// Sambungan yang siap dipakai, atau pengecualian dengan sebab yang jelas.
    ///
    /// MELEMPAR, BUKAN MENGEMBALIKAN NILAI KOSONG.
    ///
    /// Panel yang belum tertaut lalu menerima daftar unit kosong akan
    /// menggambar lab yang terlihat sepi — tidak dapat dibedakan dari lab yang
    /// memang sedang libur. Pengecualian ini ditangkap pembungkus Run di
    /// jendela dan muncul sebagai pemberitahuan yang menyebutkan sebabnya.
    /// </summary>
    private HubConnection Hub => _hub
        ?? throw new InvalidOperationException("Panel belum tertaut ke lab mana pun.");

    public Task<PanelDispatchResult> DispatchAsync(AgentCommand command, string action, string[]? targets = null)
        => Hub.InvokeAsync<PanelDispatchResult>("Dispatch", new PanelDispatch
        {
            Command = command,
            Action = action,
            Targets = targets,
        });

    /// <summary>
    /// Meneruskan satu peristiwa masukan. Tidak diaudit dan tidak dilacak
    /// sebagai batch — gerakan mouse datang puluhan kali per detik.
    /// </summary>
    public Task RelayInputAsync(string unitId, InjectInputCommand command)
        => Hub.SendAsync("RelayInput", unitId, command);

    public Task<ChatSendResult> SendChatAsync(string? unitId, string text, bool toEveryone)
        => Hub.InvokeAsync<ChatSendResult>("SendChat", unitId, text, toEveryone);

    public Task<int> ClearChatAsync() => Hub.InvokeAsync<int>("ClearChat");

    public Task<bool> AcknowledgeHelpAsync(string unitId)
        => Hub.InvokeAsync<bool>("AcknowledgeHelp", unitId);

    public Task<int> WakeUnitsAsync() => Hub.InvokeAsync<int>("WakeUnits");

    public Task<BroadcastGrant> ClaimBroadcastAsync(BroadcastClaim claim)
        => Hub.InvokeAsync<BroadcastGrant>("ClaimBroadcast", claim);

    public Task ReleaseBroadcastAsync() => Hub.SendAsync("ReleaseBroadcast");

    public Task WriteActivityAsync(string action, string? unitId = null, string? detail = null)
        => Hub.SendAsync("WriteActivity", action, unitId, detail);

    public Task<UnitSnapshot[]> GetUnitsAsync() => Hub.InvokeAsync<UnitSnapshot[]>("GetUnits");

    public Task<ActivityEntryDto[]> GetActivityAsync(int limit)
        => Hub.InvokeAsync<ActivityEntryDto[]>("GetActivity", limit);

    public Task<ChatMessage[]> GetChatHistoryAsync(string unitId)
        => Hub.InvokeAsync<ChatMessage[]>("GetChatHistory", unitId);

    public Task<bool> SetAutoFullscreenAsync(string unitId, bool enabled)
        => Hub.InvokeAsync<bool>("SetAutoFullscreen", unitId, enabled);

    public Task<bool> SetAliasAsync(string unitId, string? alias)
        => Hub.InvokeAsync<bool>("SetAlias", unitId, alias);

    /// <summary>Sesi ujian yang berjalan menurut kabar terakhir dari koordinator.</summary>
    public ExamSession? Exam { get; private set; }

    public event Action? ExamChanged;

    public Task<ExamSession?> StartExamAsync(string title)
        => Hub.InvokeAsync<ExamSession?>("StartExam", title);

    public Task<bool> EndExamAsync() => Hub.InvokeAsync<bool>("EndExam");

    public Task<ExamSession?> GetExamAsync() => Hub.InvokeAsync<ExamSession?>("GetExam");

    public Task<Dictionary<string, int>> GetExamCountsAsync(long sessionId)
        => Hub.InvokeAsync<Dictionary<string, int>>("GetExamCounts", sessionId);

    public Task<Dictionary<string, ExamUnitSummary>> GetExamSummariesAsync(long sessionId)
        => Hub.InvokeAsync<Dictionary<string, ExamUnitSummary>>("GetExamSummaries", sessionId);

    public Task<ExamEvent[]> GetExamTimelineAsync(long sessionId, string unitId)
        => Hub.InvokeAsync<ExamEvent[]>("GetExamTimeline", sessionId, unitId);

    public Task<bool> PublishAccessPolicyAsync()
        => Hub.InvokeAsync<bool>("PublishAccessPolicy");

    /// <summary>Null berarti berhasil; string berarti alasan penolakan.</summary>
    public Task<string?> RenameUnitAsync(string unitId, string newUnitId)
        => Hub.InvokeAsync<string?>("RenameUnit", unitId, newUnitId);

    public Task<Dictionary<string, string>> GetMacAddressesAsync()
        => Hub.InvokeAsync<Dictionary<string, string>>("GetMacAddresses");

    public Task<PanelSummary[]> GetPanelsAsync() => Hub.InvokeAsync<PanelSummary[]>("GetPanels");

    public Task<bool> ApprovePanelAsync(string deviceId, PanelRole role)
        => Hub.InvokeAsync<bool>("ApprovePanel", deviceId, role);

    public Task<bool> RevokePanelAsync(string deviceId)
        => Hub.InvokeAsync<bool>("RevokePanel", deviceId);

    public Task<EnrollmentWindowDto> OpenEnrollmentAsync()
        => Hub.InvokeAsync<EnrollmentWindowDto>("OpenEnrollment");

    public Task<EnrollmentWindowDto> CloseEnrollmentAsync()
        => Hub.InvokeAsync<EnrollmentWindowDto>("CloseEnrollment");

    public Task<EnrollmentWindowDto> GetEnrollmentAsync()
        => Hub.InvokeAsync<EnrollmentWindowDto>("GetEnrollment");

    /* ================= jaringan ================= */

    /// <summary>
    /// Menebak apakah panel ini tersambung lewat kabel.
    ///
    /// Dipakai Superadmin untuk memperingatkan sebelum siaran 1080p dimulai,
    /// karena siaran itu menuntut sekitar 45 Mbps uplink dan Wi-Fi lab berbagi
    /// airtime dengan seluruh kelas. Peringatan, bukan penolakan — dosen yang
    /// mendapati kualitasnya turun sendiri tanpa penjelasan akan menyimpulkan
    /// aplikasinya rusak.
    /// </summary>
    private static bool IsWired()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                            or NetworkInterfaceType.GigabitEthernet
                && !nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException)
        {
            // Tidak tahu lebih jujur daripada menebak "kabel" dan membuat
            // peringatan Wi-Fi tidak pernah muncul di mesin yang justru butuh.
            return false;
        }
    }

    // Bukan lewat properti Hub: membuang objek yang belum pernah tersambung
    // bukan kesalahan, jadi tidak boleh melempar.
    public async ValueTask DisposeAsync() => await TeardownAsync();
}

/// <summary>Memuat panel.json, melengkapi bagian yang belum terisi.</summary>
public static class PanelIdentity
{
    public static PanelConfig LoadOrCreate()
    {
        var panel = ConfigStore.LoadOrCreate<PanelConfig>(RetrivaPaths.PanelIdentityFile);

        var dirty = false;

        // DeviceId dibuat acak, bukan diturunkan dari nama mesin atau alamat
        // MAC. Nama mesin berubah saat PC dinamai ulang, dan MAC berubah saat
        // orang berpindah dari kabel ke Wi-Fi — keduanya akan membuat panel
        // yang sama muncul sebagai perangkat baru yang menunggu persetujuan,
        // tepat pada saat dosennya sedang berdiri di depan kelas.
        if (string.IsNullOrWhiteSpace(panel.DeviceId))
        {
            panel.DeviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(panel.DisplayName))
        {
            panel.DisplayName = Environment.MachineName;
            dirty = true;
        }

        if (dirty) ConfigStore.Save(RetrivaPaths.PanelIdentityFile, panel);
        return panel;
    }
}
