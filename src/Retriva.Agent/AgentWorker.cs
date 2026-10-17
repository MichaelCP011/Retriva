using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using Retriva.Agent.Power;
using Retriva.Agent.Session;
using Retriva.Media;
using Retriva.Shared.Local;
using Retriva.Shared;
using Retriva.Shared.Broadcast;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;
using Retriva.Shared.Security;
using Retriva.Shared.Update;

namespace Retriva.Agent;

/// <summary>
/// Loop utama agent: menyambung ke konsol, mendaftar, lalu mengirim heartbeat.
/// Arah koneksi sengaja keluar — agent yang menelepon konsol, bukan sebaliknya,
/// sehingga hanya PC admin yang butuh aturan firewall masuk.
/// </summary>
public sealed class AgentWorker(ILogger<AgentWorker> log) : BackgroundService
{
    private AgentConfig _config = new();
    private HubConnection? _connection;
    private string _unitId = "PC-00";

    /// <summary>Selang pratinjau yang ditentukan konsol saat pendaftaran.</summary>
    private int _thumbnailSeconds = 2;

    private IPowerControl _power = null!;
    private SessionBridge _session = null!;

    /// <summary>
    /// Perintah yang menunggu balasan klien, agar hasilnya dapat diteruskan ke
    /// konsol dengan CommandId yang benar.
    ///
    /// Namanya dulu _pendingLocks, dari masa ketika hanya kunci layar yang
    /// memakainya. Sekarang siaran, kendali jarak jauh, dan presentasi memakai
    /// jalur yang sama — dan nama yang menyebut satu di antaranya membuat siapa
    /// pun yang membacanya mengira dua lainnya punya jalur sendiri.
    /// </summary>
    private readonly Dictionary<Guid, Guid> _awaitingClientAck = [];

    /// <summary>Menandai kunci sedang aktif. Ctrl+Alt+Del tidak dapat diblokir
    /// dari mode pengguna, jadi mahasiswa masih bisa menutup klien lewat Task
    /// Manager. Yang bisa dilakukan bukan mencegah, melainkan melaporkannya.</summary>
    private bool _lockActive;
    private string? _lockMessage;
    private string? _lockIssuer;
    private bool _activeRemote;

    /// <summary>
    /// Frame terakhir sebelum layar dikunci.
    ///
    /// Saat terkunci, overlay menutupi seluruh layar sehingga pratinjau akan
    /// berubah menjadi gambar layar kunci — dan bila 40 unit dikunci bersamaan,
    /// dashboard berisi 40 gambar identik yang tidak memberi informasi apa pun.
    /// Membekukan frame terakhir justru menjawab pertanyaan yang sebenarnya
    /// dimiliki admin: mahasiswa ini tadi sedang membuka apa?
    /// </summary>
    private byte[] _lastThumbnail = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _config = ConfigStore.LoadOrCreate<AgentConfig>(
            RetrivaPaths.AgentConfigFile,
            problem => log.LogError("{Problem}", problem));

        // Penimpaan untuk pengembangan, tidak pernah ditulis kembali ke berkas.
        //   $env:RETRIVA_CONSOLE = "127.0.0.1"
        //   dotnet run --project src/Retriva.Agent -- --console 127.0.0.1 --unit PC-01
        var consoleOverride = Environment.GetEnvironmentVariable("RETRIVA_CONSOLE") ?? Arg("--console");
        if (!string.IsNullOrWhiteSpace(consoleOverride))
        {
            _config.ConsoleEndpoints = [consoleOverride];
            log.LogWarning("Endpoint konsol ditimpa menjadi {Endpoint}", consoleOverride);
        }

        var unitOverride = Environment.GetEnvironmentVariable("RETRIVA_UNIT") ?? Arg("--unit");
        if (!string.IsNullOrWhiteSpace(unitOverride)) _config.UnitIdOverride = unitOverride;

        if (ConsoleConnection.TryAdoptLocalTrust(_config, log))
            ConfigStore.Save(RetrivaPaths.AgentConfigFile, _config);

        /*
            Nama sementara sampai koordinator memberi yang sah.

            Penurunan dari alamat IP DIHAPUS dari sini. Ia memakai
            `new ConsoleConfig()` — konfigurasi baru dengan nilai bawaan — jadi
            ia tidak pernah tahu rentang alamat lab yang sebenarnya, dan setelan
            asisten di layar Konfigurasi tidak berpengaruh sama sekali.

            Yang mengetahui rentangnya adalah koordinator, dan ia juga sudah tahu
            alamat sambungan agent ini. Jadi ia yang menamai, lewat
            ConfirmedUnitId di jawaban pendaftaran.
        */
        _unitId = _config.UnitIdOverride ?? Environment.MachineName;

        // Mode simulasi dapat dipaksa lewat lingkungan tanpa mengubah berkas,
        // supaya konfigurasi produksi tetap bersih saat dipakai untuk pengembangan.
        var powerMode = Environment.GetEnvironmentVariable("RETRIVA_POWER") ?? _config.PowerControlMode;
        var simulate = string.Equals(powerMode, "simulate", StringComparison.OrdinalIgnoreCase);
        _power = simulate ? new SimulatedPowerControl(log) : new WindowsPowerControl(log);

        // Dicetak apa adanya supaya tidak perlu menebak konfigurasi mana yang terbaca.
        log.LogInformation("Konfigurasi : {Path}", RetrivaPaths.AgentConfigFile);
        log.LogInformation("Unit        : {UnitId}", _unitId);
        log.LogInformation("Endpoint    : {Endpoints}", string.Join(", ", _config.ConsoleEndpoints));
        log.LogInformation("Sidik jari  : {Pin}",
            string.IsNullOrWhiteSpace(_config.ConsoleCertificateThumbprint)
                ? "(kosong)"
                : _config.ConsoleCertificateThumbprint[..16] + "…");
        log.LogInformation("Token       : {Token}",
            string.IsNullOrWhiteSpace(_config.DeviceToken) ? "(belum ada)" : "tersimpan");
        log.LogInformation("Kontrol daya: {Mode}", simulate ? "SIMULASI — tidak dieksekusi" : "NYATA");

        // Penolakan sematan sertifikat dicetak lengkap. Tanpa ini ia keluar
        // sebagai galat SSL generik yang tidak menyebut sidik jari sama sekali.
        CertificatePin.Mismatch += (expected, actual) => log.LogError(
            "Sidik jari sertifikat TIDAK COCOK.\n  diharapkan : {Expected}\n  diterima   : {Actual}\n"
          + "  Konsol kemungkinan menerbitkan sertifikat baru. Hapus "
          + "ConsoleCertificateThumbprint di agent.json agar diambil ulang.",
            expected, actual);

        // Jembatan ke sesi pengguna. Layanan tidak dapat menampilkan jendela
        // maupun menyuntikkan input, jadi seluruh fitur visual dijalankan klien.
        _session = new SessionBridge(log)
        {
            // Diselesaikan SEKALI di sini, lalu dipakai kedua sisi. Menyelesaikan
            // ulang di klien berarti dua rumus yang harus tetap sama selamanya.
            FileInbox = FileDownloader.ResolveInbox(_config.FileInboxDirectory, null, log),
        };
        _session.AckReceived += OnClientAck;
        _session.ClientHelloReceived += hello =>
        {
            if (!string.IsNullOrWhiteSpace(hello.DocumentsDirectory))
            {
                var resolved = FileDownloader.ResolveInbox(_config.FileInboxDirectory, hello.DocumentsDirectory, log);
                _session.FileInbox = resolved;
                log.LogInformation("Folder berkas mahasiswa diperbarui: {Dir}", resolved);
            }
        };

        // Diteruskan sebagai laporan tanpa perintah, karena memang tidak ada
        // perintah yang memicunya. Tanpa ini panel admin akan terus menampilkan
        // sesi yang sudah mati — mahasiswa menekan pintasan keluar, aliran layar
        // berhenti, dan admin hanya melihat frame terakhir yang membeku.
        _session.RemoteEndedReceived += reason =>
        {
            _activeRemote = false;
            ReportToConsole(CommandOutcome.Success, $"kendali.berakhir: {reason}");
        };

        // Pesan dan permintaan bantuan diteruskan apa adanya ke konsol. Agent
        // tidak menyimpan maupun menafsirkannya — ia hanya jembatan, dan
        // menyimpan salinan di sini berarti dua sumber kebenaran yang harus
        // disamakan setiap kali salah satunya berubah.
        // Pengamatan ujian diteruskan apa adanya, tanpa disimpan maupun
        // ditafsirkan agent. Sama seperti chat: agent adalah jembatan, dan
        // menyimpan salinan di sini berarti dua sumber kebenaran.
        _session.ExamObserved += observation =>
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;
            _ = _connection.SendAsync("ReportExamEvent", new ExamEvent
            {
                UnitId = _unitId,
                Kind = observation.Kind,
                Description = observation.Description,
            });
        };

        _session.ChatReceived += chat =>
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;
            _ = _connection.SendAsync("PublishChat", new ChatMessage
            {
                MessageId = chat.MessageId,
                UnitId = _unitId,
                FromAdmin = false,
                DisplayName = _session.StudentName ?? _session.WindowsUser ?? _unitId,
                Text = chat.Text,
            });
        };

        _session.ThumbnailReceived += async thumb =>
        {
            _lastThumbnail = thumb.JpegData;
            if (_connection is { State: HubConnectionState.Connected })
            {
                try
                {
                    await _connection.SendAsync("PublishThumbnail", new ThumbnailFrame
                    {
                        UnitId = _unitId,
                        JpegData = thumb.JpegData,
                        Width = thumb.Width,
                        Height = thumb.Height,
                    });
                }
                catch (Exception ex)
                {
                    log.LogDebug("Pengiriman pratinjau dari klien gagal: {Message}", ex.Message);
                }
            }
        };

        _session.HelpReceived += raised =>
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;
            _ = _connection.SendAsync("RaiseHand", new HelpRequest
            {
                UnitId = _unitId,
                Raised = raised,
                DisplayName = _session.StudentName ?? _session.WindowsUser,
            });
        };
        _session.SelfNameReceived += name =>
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;

            /*
                Dikirim dengan FireAndLog, bukan "_ = SendAsync(...)".

                Pola yang dibuang itu membuang Task-nya beserta setiap
                pengecualian di dalamnya. Mahasiswa akan melihat namanya
                tersimpan di layarnya sendiri sementara koordinator tidak pernah
                menerimanya, dan tidak ada satu baris pun di log manapun yang
                menyebutkan kegagalannya.
            */
            FireAndLog(_connection.SendAsync("SetUnitSelfName", _unitId, name),
                "mengirim nama unit dari mahasiswa");
        };

        _session.ConnectionChanged += connected =>
        {
            log.LogInformation("Klien sesi {State}", connected ? "tersambung" : "terputus");

            if (!connected)
            {
                if (_lockActive)
                {
                    log.LogWarning("Klien menghilang saat layar terkunci — kemungkinan ditutup paksa. Memanggil watchdog untuk meluncurkan ulang.");
                    ReportToConsole(CommandOutcome.Failed,
                        "klien ditutup saat layar terkunci — memicu peluncuran ulang (watchdog)");
                }

                // Otomatis luncurkan ulang jika klien mati/di-kill
                _ = Task.Run(() => ClientLauncher.EnsureRunning(log));
            }
            else
            {
                if (_lockActive)
                {
                    log.LogInformation("Klien tersambung kembali saat status terkunci aktif — mengirim ulang perintah kunci layar.");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        await _session.SendAsync(new ShowLockScreen
                        {
                            Message = _lockMessage ?? "Layar dikunci oleh Admin",
                            IssuedBy = _lockIssuer ?? "Admin",
                        });
                    });
                }
            }
        };
        _session.Start(_unitId, () => _connection?.State == HubConnectionState.Connected);
        _ = Task.Run(() => ClientLauncher.EnsureRunning(log));
        _ = Task.Run(() => PeriodicUpdateCheckerAsync(stoppingToken), stoppingToken);

        var attempt = 0;
        string? dynamicThumbprint = _config.ConsoleCertificateThumbprint;
        int dynamicPort = _config.ConsolePort;

        while (!stoppingToken.IsCancellationRequested)
        {
            var connected = false;

            // 1. Coba sambungkan ke endpoint yang sudah diketahui bila ada dan sidik jarinya tersedia
            if (_config.ConsoleEndpoints.Count > 0 && !string.IsNullOrWhiteSpace(dynamicThumbprint))
            {
                foreach (var endpoint in _config.ConsoleEndpoints.ToList())
                {
                    if (stoppingToken.IsCancellationRequested) return;
                    try
                    {
                        await RunAgainstAsync(endpoint, dynamicThumbprint, dynamicPort, stoppingToken);
                        connected = true;
                        attempt = 0;
                        break;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        log.LogWarning("Gagal terhubung ke endpoint tersimpan {Endpoint}: {Chain}",
                            endpoint, DescribeChain(ex));
                    }
                }
            }

            // 2. Bila belum tersambung (karena berpindah jaringan/lab atau instalasi baru), cari Superadmin di jaringan lokal
            if (!connected && !stoppingToken.IsCancellationRequested)
            {
                log.LogInformation("Mencari Superadmin di jaringan lokal (desentralisasi)...");
                var discovered = await Task.Run(
                    () => LabRediscovery.FindAny(TimeSpan.FromSeconds(3), log),
                    stoppingToken);

                if (discovered is not null)
                {
                    log.LogInformation("Menemukan Superadmin {LabName} di {Host}:{Port}",
                        discovered.LabName, discovered.Host, discovered.Port);

                    dynamicThumbprint = discovered.Thumbprint;
                    dynamicPort = discovered.Port;

                    if (!_config.ConsoleEndpoints.Contains(discovered.Host))
                    {
                        _config.ConsoleEndpoints.Insert(0, discovered.Host);
                    }

                    try
                    {
                        await RunAgainstAsync(discovered.Host, dynamicThumbprint, dynamicPort, stoppingToken);
                        connected = true;
                        attempt = 0;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        log.LogWarning("Gagal terhubung ke Superadmin {Host}:{Port}: {Chain}",
                            discovered.Host, discovered.Port, DescribeChain(ex));
                    }
                }
            }

            if (!connected)
            {
                attempt++;
                var delay = JitteredRetryPolicy.EndpointRetryDelay(attempt);
                log.LogInformation("Belum terhubung ke Superadmin mana pun. Mencoba lagi dalam {Seconds:F0} detik...", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Merangkai seluruh pesan pengecualian dari luar ke dalam.
    ///
    /// Dibatasi lima lapis: lebih dalam dari itu isinya sudah pengulangan, dan
    /// baris log yang terlalu panjang justru menyembunyikan bagian pentingnya
    /// dengan cara yang berbeda.
    /// </summary>
    /// <summary>
    /// Menjalankan pengiriman ke koordinator dan MENCATAT kegagalannya.
    ///
    /// Pengganti pola "_ = connection.SendAsync(...)", yang membuang Task-nya
    /// beserta setiap pengecualian di dalamnya. Sambungan yang putus tepat saat
    /// pesan dikirim lalu menjadi kegagalan yang tidak muncul di mana pun: tidak
    /// di log agent, tidak di panel, dan tidak pada layar orang yang menekan
    /// tombolnya.
    /// </summary>
    private void FireAndLog(Task work, string what) =>
        _ = work.ContinueWith(t =>
                log.LogWarning("Gagal {What}: {Reason}", what, DescribeChain(t.Exception!)),
            TaskContinuationOptions.OnlyOnFaulted);

    private static string DescribeChain(Exception ex)
    {
        var parts = new List<string>();

        for (var e = ex; e is not null && parts.Count < 5; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");

        return string.Join(" <- ", parts);
    }

    /// <summary>
    /// Host konsol yang benar-benar dipakai, bukan yang ditebak.
    ///
    /// Kendali jarak jauh butuh alamat tujuan untuk aliran layar, dan panel
    /// admin tidak dapat menentukannya sendiri: mesin dengan beberapa kartu
    /// jaringan punya beberapa alamat, dan hanya agent yang tahu lewat mana ia
    private string? _consoleHost;
    private string? _activeThumbprint;
    private int? _activePort;

    /// <summary>Mengambil bagian host dari URL endpoint konsol.</summary>
    private static string? HostFromEndpoint(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return uri.Host;

        // Endpoint bisa juga ditulis tanpa skema, misalnya "10.0.0.5:8443".
        var colon = endpoint.LastIndexOf(':');
        var host = colon > 0 ? endpoint[..colon] : endpoint;
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    /// <summary>
    /// Menerjemahkan jenis peristiwa protokol konsol ke protokol lokal.
    ///
    /// Kedua enum sengaja tidak disatukan: yang satu bagian kontrak jaringan
    /// antarmesin, yang satu kontrak antarproses di satu mesin. Menyatukannya
    /// membuat perubahan di satu sisi memaksa perubahan di sisi lain tanpa
    /// alasan yang sebenarnya.
    /// </summary>
    private static RemoteInputKind MapInputKind(InputEventKind kind) => kind switch
    {
        InputEventKind.MouseMove => RemoteInputKind.MouseMove,
        InputEventKind.MouseDown => RemoteInputKind.MouseDown,
        InputEventKind.MouseUp => RemoteInputKind.MouseUp,
        InputEventKind.MouseWheel => RemoteInputKind.MouseWheel,
        InputEventKind.KeyDown => RemoteInputKind.KeyDown,
        _ => RemoteInputKind.KeyUp,
    };

    private async Task RunAgainstAsync(string endpoint, string? thumbprint, int? port, CancellationToken ct)
    {
        _consoleHost = HostFromEndpoint(endpoint);
        _activeThumbprint = !string.IsNullOrWhiteSpace(thumbprint) ? thumbprint : _config.ConsoleCertificateThumbprint;
        _activePort = port is > 0 ? port : _config.ConsolePort;
        _connection = ConsoleConnection.Build(endpoint, _config, RetrivaProtocol.HubPath, _activeThumbprint, _activePort);
        _connection.On<AgentCommand>("Execute", async command => await HandleAsync(command));

        /*
            Balasan sesederhana mungkin, dan itu disengaja.

            Yang diukur koordinator adalah waktu pulang-pergi, jadi apa pun yang
            dikerjakan di sini ikut terhitung sebagai latensi jaringan. Membaca
            satu nilai saja — apalagi menyentuh disk atau kartu jaringan — akan
            membuat unit yang sibuk tampak seperti unit yang jauh.
        */
        _connection.On("Ping", () => true);

        // Aturan akses berubah di tengah jalan, tanpa menunggu unit mendaftar
        // ulang. Tanpa jalur ini, asisten yang mematikan kewajiban login harus
        // menyalakan ulang empat puluh PC agar berlaku.
        _connection.On<AccessPolicy>("Policy", async policy =>
        {
            log.LogInformation("Setelan akses diperbarui: login={Login} tamu={Guest} tolakKendali={Reject}",
                policy.RequireStudentLogin, policy.AllowGuestAccess, policy.StudentsMayRejectControl);
            await _session.SetPolicyAsync(policy);
        });

        /*
            Diblokir: dicatat dengan alasannya, bukan sekadar terputus.

            Koordinator memutus sambungan agent yang diblokir. Tanpa pesan ini
            agent hanya melihat sambungannya hilang, lalu mencoba lagi setiap
            beberapa detik selamanya — dan log-nya berisi puluhan baris "mencoba
            menyambung ulang" yang tidak dapat dibedakan dari jaringan yang
            sedang bermasalah.
        */
        _connection.On<string>("Blocked", reason =>
            log.LogWarning("DIBLOKIR koordinator: {Reason}. "
                         + "Penyambungan berikutnya akan ditolak sampai blokirnya dilepas.", reason));

        // Reconnect otomatis menghasilkan connection id baru di sisi server,
        // jadi unit harus mendaftar ulang agar perintah tetap sampai kepadanya.
        _connection.Reconnected += async _ =>
        {
            log.LogInformation("Koneksi pulih, mendaftar ulang");
            try { await RegisterAsync(CancellationToken.None); }
            catch (Exception ex) { log.LogWarning("Pendaftaran ulang gagal: {Message}", ex.Message); }
        };

        _connection.Reconnecting += error =>
        {
            log.LogWarning("Koneksi terputus, mencoba menyambung ulang: {Message}", error?.Message);
            return Task.CompletedTask;
        };

        await _connection.StartAsync(ct);
        log.LogInformation("Tersambung ke konsol {Endpoint}", endpoint);

        if (!await RegisterAsync(ct))
        {
            await _connection.StopAsync(ct);
            return;
        }

        // Pratinjau berjalan di task terpisah dari heartbeat: selangnya berbeda,
        // dan pengambilan gambar yang tersendat tidak boleh ikut menunda denyut status.
        _ = Task.Run(() => PublishThumbnailsAsync(_thumbnailSeconds, ct), ct);

        // Bertahan selama koneksi belum benar-benar mati. Status Reconnecting
        // tidak boleh mengakhiri loop, karena penyambungan ulang ditangani
        // SignalR sendiri dan connection id lama akan digantikan.
        while (!ct.IsCancellationRequested
               && _connection.State != HubConnectionState.Disconnected)
        {
            if (_connection.State == HubConnectionState.Connected)
            {
                if (!_session.ClientConnected)
                {
                    ClientLauncher.EnsureRunning(log);
                }

                try
                {
                    await _connection.SendAsync("Heartbeat", new HeartbeatReport
                    {
                        UnitId = _unitId,
                        // Status dilaporkan apa adanya, bukan diasumsikan konsol.
                        // Dengan begitu dashboard menampilkan keadaan sesungguhnya
                        // meski perintah dikirim dari PC admin yang lain.
                        Status = _lockActive ? UnitStatus.Locked : UnitStatus.Online,
                        LoggedOnUser = _session.WindowsUser ?? Environment.UserName,
                        RetrivaUserNim = _session.StudentNim,
                        RetrivaUserName = _session.StudentName,
                        IsGuest = _session.StudentIsGuest,

                        // Dilaporkan apa adanya dari klien. Agent tidak
                        // menyimpulkannya sendiri: ia hanya meneruskan perintah
                        // membuka penonton dan tidak tahu apakah frame tiba.
                        BroadcastFrames = _session.BroadcastFrames,
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug("Heartbeat gagal: {Message}", ex.Message);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(RetrivaProtocol.HeartbeatSeconds), ct);
        }

        log.LogWarning("Koneksi ke {Endpoint} berakhir", endpoint);
    }

    /// <summary>
    /// Mengirim pratinjau layar secara berkala. Berjalan di task terpisah dari
    /// heartbeat karena selangnya berbeda dan pengambilan gambar bisa tersendat
    /// tanpa boleh ikut menunda denyut status.
    /// </summary>
    private async Task PublishThumbnailsAsync(int intervalSeconds, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));

        while (!ct.IsCancellationRequested
               && _connection is { State: not HubConnectionState.Disconnected })
        {
            if (_connection.State == HubConnectionState.Connected && !_session.ClientConnected)
            {
                try
                {
                    // Tetap ambil tangkapan layar langsung karena LockOverlay telah dikecualikan
                    // dari tangkapan layar (WDA_EXCLUDEFROMCAPTURE). Dengan demikian admin tetap
                    // dapat memantau dan menginspeksi aktivitas mahasiswa di balik layar terkunci.
                    var jpeg = ScreenThumbnail.Capture(log);
                    if (jpeg.Length > 0) _lastThumbnail = jpeg;
                    else if (_lastThumbnail.Length > 0) jpeg = _lastThumbnail;

                    if (jpeg.Length > 0)
                    {
                        await _connection.SendAsync("PublishThumbnail", new ThumbnailFrame
                        {
                            UnitId = _unitId,
                            JpegData = jpeg,
                            Width = ScreenThumbnail.Width,
                            Height = ScreenThumbnail.Height,
                        }, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug("Pengiriman pratinjau gagal: {Message}", ex.Message);
                }
            }

            await Task.Delay(interval, ct);
        }
    }

    /// <summary>
    /// Mendaftar ke konsol. Dipanggil saat sambungan pertama dan setiap kali
    /// SignalR berhasil menyambung ulang.
    /// </summary>
    private async Task<bool> RegisterAsync(CancellationToken ct)
    {
        if (_connection is null) return false;

        var ip = LocalIpAddress(_consoleHost ?? "");
        var link = DescribeLink(ip);

        log.LogInformation("Tautan jaringan: {Adapter} · {Kind} · {Speed} · MAC {Mac}",
            string.IsNullOrWhiteSpace(link.Adapter) ? "adapter tidak dikenali" : link.Adapter,
            link.Wired ? "kabel" : "nirkabel",
            link.SpeedMbps > 0 ? $"{link.SpeedMbps} Mbps" : "kecepatan tidak dilaporkan",
            link.Mac);

        string? tokenToSend = null;
        if (!string.IsNullOrWhiteSpace(_activeThumbprint) && _config.LabTokens.TryGetValue(_activeThumbprint, out var tok))
            tokenToSend = tok;
        else if (!string.IsNullOrWhiteSpace(_config.DeviceToken))
            tokenToSend = _config.DeviceToken;

        var response = await _connection.InvokeAsync<RegisterResponse>("Register", new RegisterRequest
        {
            UnitId = _unitId,
            HostName = Environment.MachineName,
            IpAddress = ip,
            MacAddress = link.Mac,
            WiredConnection = link.Wired,
            LinkSpeedMbps = link.SpeedMbps,
            NetworkAdapter = link.Adapter,
            AgentVersion = typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            EnrollmentSecret = string.IsNullOrWhiteSpace(tokenToSend) ? _config.EnrollmentSecret : null,
            DeviceToken = tokenToSend,
        }, ct);

        if (!response.Accepted)
        {
            log.LogError("Pendaftaran ditolak: {Reason}", response.RejectReason);
            return false;
        }

        // Token diterbitkan sekali; simpan untuk lab ini agar dapat roaming antar-lab
        if (!string.IsNullOrWhiteSpace(response.IssuedDeviceToken))
        {
            if (!string.IsNullOrWhiteSpace(_activeThumbprint))
                _config.LabTokens[_activeThumbprint] = response.IssuedDeviceToken;

            _config.DeviceToken = response.IssuedDeviceToken;
            try
            {
                ConfigStore.Save(RetrivaPaths.AgentConfigFile, _config);
                log.LogInformation("Token perangkat tersimpan untuk lab ini");
            }
            catch (Exception ex)
            {
                log.LogWarning("Gagal menyimpan token ke file konfigurasi: {Message}", ex.Message);
            }
        }

        _unitId = response.ConfirmedUnitId ?? _unitId;
        _thumbnailSeconds = Math.Max(1, response.ThumbnailSeconds);

        // Ukuran pratinjau ditentukan konsol, bukan dipatok agent. Kebutuhan
        // pengawasan saat ujian berbeda dari kebutuhan sehari-hari, dan yang
        // tahu bedanya adalah orang di depan panel.
        /*
            Nama dari koordinator DIPAKAI, tidak diabaikan.

            ConfirmedUnitId sudah ada di protokol sejak lama beserta komentar
            "nomor unit yang diakui konsol; bisa berbeda bila deteksi agent
            salah" — dan tidak ada satu pun baris yang membacanya. Jadi ketika
            deteksi agent memang salah, koordinator memperbaikinya dan agent
            tetap memakai nama kelirunya, termasuk di seluruh log dan pesan
            berikutnya.

            Penimpaan lewat --unit tetap menang: itu alat pengembangan, dan alat
            pengembangan yang dapat dibatalkan diam-diam oleh server tidak
            berguna untuk menguji apa pun.
        */
        if (_config.UnitIdOverride is null
            && response.ConfirmedUnitId is { Length: > 0 } confirmed
            && confirmed != _unitId)
        {
            log.LogInformation("Nama unit diperbarui koordinator: {Lama} -> {Baru}", _unitId, confirmed);
            _unitId = confirmed;
        }

        // Aturan akses disimpan sebelum klien pertama menyambung. Klien yang
        // menyambung lebih dulu akan menerimanya lewat AgentWelcome; yang sudah
        // tersambung menerimanya sebagai pembaruan.
        await _session.SetPolicyAsync(response.Policy);

        ScreenThumbnail.Resize(response.ThumbnailWidth);
        log.LogInformation("Pratinjau: {Width}x{Height} tiap {Seconds} detik",
            ScreenThumbnail.Width, ScreenThumbnail.Height, _thumbnailSeconds);
        log.LogInformation("Terdaftar sebagai {UnitId}", _unitId);
        return true;
    }

    /// <summary>
    /// Satu-satunya tempat perintah ditangani. Menambah fitur berarti menambah
    /// satu cabang di sini dan satu turunan AgentCommand di Shared.
    /// </summary>
    private async Task HandleAsync(AgentCommand command)
    {
        log.LogInformation("Perintah masuk: {Type} dari {IssuedBy}",
            command.GetType().Name, command.IssuedBy);

        var outcome = CommandOutcome.Unsupported;
        string? detail;

        switch (command)
        {
            case PingCommand:
                outcome = CommandOutcome.Success;
                detail = $"pong dari {_unitId}";
                log.LogInformation("Ping dari {IssuedBy}", command.IssuedBy);
                break;

            case ShutdownCommand c:
            {
                var r = _power.Shutdown(c.DelaySeconds, c.Force, c.Message);
                outcome = r.Success ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = r.Detail;
                break;
            }

            case RestartCommand c:
            {
                var r = _power.Restart(c.DelaySeconds, c.Message);
                outcome = r.Success ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = r.Detail;
                break;
            }

            case AbortShutdownCommand:
            {
                var r = _power.Abort();
                outcome = r.Success ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = r.Detail;
                break;
            }

            case LockScreenCommand c:
            {
                var sent = await _session.SendAsync(new ShowLockScreen
                {
                    CommandId = command.CommandId,
                    Message = c.Message,
                    IssuedBy = c.IssuedBy,
                });
                log.LogInformation("Kunci diteruskan ke klien: {Sent} (klien tersambung: {Connected})",
                    sent, _session.ClientConnected);
                if (sent)
                {
                    _lockActive = true;
                    _lockMessage = c.Message;
                    _lockIssuer = c.IssuedBy;
                    RememberAwaitingAck(command.CommandId);
                    return;
                }
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case UnlockScreenCommand:
            {
                var sent = await _session.SendAsync(new HideLockScreen
                {
                    CommandId = command.CommandId,
                });
                if (sent)
                {
                    _lockActive = false;
                    _lockMessage = null;
                    _lockIssuer = null;
                    RememberAwaitingAck(command.CommandId);
                    return;
                }
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case StartBroadcastCommand c:
            {
                // Agent hanya meneruskan perintahnya. Byte video mengalir
                // langsung dari PC admin ke klien lewat multicast, tidak pernah
                // melewati agent — merelaynya akan menggandakan beban tanpa guna.
                var sent = await _session.SendAsync(new ShowBroadcast
                {
                    CommandId = command.CommandId,
                    MulticastGroup = c.MulticastGroup,
                    Port = c.Port,
                    SourceLabel = c.SourceLabel,
                    // Agent memeriksa namanya sendiri di daftar. Koordinator
                    // tidak dapat mengirim perintah berbeda ke tiap unit tanpa
                    // memecah satu siaran menjadi empat puluh pengiriman.
                    Fullscreen = c.FullscreenUnits.Contains(_unitId, StringComparer.OrdinalIgnoreCase),
                });
                outcome = sent ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = sent
                    ? $"penonton dibuka pada {c.MulticastGroup}:{c.Port}"
                    : "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case BeginExamCommand c:
            {
                var sent = await _session.SendAsync(new BeginExamWatch
                {
                    CommandId = command.CommandId,
                    SessionId = c.SessionId,
                    Title = c.Title,
                });

                log.LogInformation("Sesi ujian diteruskan ke klien: {Sent} · {Title}", sent, c.Title);
                if (sent) { RememberAwaitingAck(command.CommandId); return; }

                // Kegagalannya disebut apa adanya. Unit tanpa RetrivaClient tidak
                // dipantau sama sekali, dan panel harus melihat itu sebagai unit
                // yang tidak diawasi — bukan sebagai unit yang bersih.
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna — unit ini TIDAK dipantau";
                break;
            }

            case EndExamCommand:
            {
                var sent = await _session.SendAsync(new EndExamWatch
                {
                    CommandId = command.CommandId,
                });
                log.LogInformation("Akhir sesi ujian diteruskan ke klien: {Sent}", sent);
                if (sent) { RememberAwaitingAck(command.CommandId); return; }

                // Klien yang sudah tidak ada berarti pemantauannya juga sudah
                // tidak ada — prosesnya mati bersama hook-nya. Ini bukan
                // kegagalan yang perlu dilaporkan sebagai masalah.
                outcome = CommandOutcome.Success;
                detail = "klien tidak berjalan — pemantauan sudah berhenti";
                break;
            }

            case StartPresentingCommand c:
            {
                // Diteruskan apa adanya ke klien. Agent tidak dapat menangkap
                // layar — ia layanan tanpa sesi desktop, dan tidak melihat satu
                // piksel pun dari layar mahasiswa.
                var sent = await _session.SendAsync(new BeginPresenting
                {
                    CommandId = command.CommandId,
                    RelayHost = c.RelayHost,
                    RelayPort = c.RelayPort,
                    Width = c.Width,
                    FrameRate = c.FrameRate,
                    Quality = c.Quality,
                    RequestedBy = c.IssuedBy,
                });

                log.LogInformation("Presentasi diteruskan ke klien: {Sent} · relay {Host}:{Port}",
                    sent, c.RelayHost, c.RelayPort);

                if (sent) { RememberAwaitingAck(command.CommandId); return; }

                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case StopPresentingCommand:
            {
                var sent = await _session.SendAsync(new EndPresenting
                {
                    CommandId = command.CommandId,
                });
                if (sent) { RememberAwaitingAck(command.CommandId); return; }

                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case StopBroadcastCommand:
            {
                var sent = await _session.SendAsync(new HideBroadcast
                {
                    CommandId = command.CommandId,
                });
                outcome = sent ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = sent ? "penonton ditutup" : "RetrivaClient tidak berjalan";
                break;
            }

            case BeginRemoteCommand c:
            {
                if (_consoleHost is null)
                {
                    outcome = CommandOutcome.Failed;
                    detail = "alamat konsol belum diketahui";
                    break;
                }

                var targetHost = !string.IsNullOrWhiteSpace(c.AdminHost) ? c.AdminHost : _consoleHost;

                var sent = await _session.SendAsync(new BeginRemoteSession
                {
                    CommandId = command.CommandId,
                    AdminHost = targetHost,
                    Port = FramePacket.RemoteControlPort,
                    FrameRate = c.FrameRate,
                    AllowInput = c.AllowInput,
                    IssuedByName = string.IsNullOrWhiteSpace(c.IssuedBy) ? "Admin" : c.IssuedBy,

                    // Aturannya dari koordinator, bukan dari klien. Klien yang
                    // memutuskan sendiri berarti satu PC dapat menuntut izin
                    // sementara yang lain tidak.
                    NeedsConsent = _session.Policy.StudentsMayRejectControl,
                });

                // Hasil sesungguhnya datang lewat ack dari klien, bukan dari
                // sini — yang bisa dipastikan di titik ini hanya bahwa pesannya
                // masuk ke pipa. Karena itu laporannya ditunda, pola yang sama
                // seperti kunci layar: melaporkan sukses sekarang berarti
                // mengaku berhasil sebelum ada yang benar-benar terjadi.
                if (sent) { RememberAwaitingAck(command.CommandId); return; }
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case EndRemoteCommand:
            {
                var sent = await _session.SendAsync(new EndRemoteSession
                {
                    CommandId = command.CommandId,
                });
                if (sent) { RememberAwaitingAck(command.CommandId); return; }
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan";
                break;
            }

            case SendChatCommand c:
            {
                var sent = await _session.SendAsync(new DeliverChat
                {
                    CommandId = command.CommandId,
                    Text = c.Text,
                    FromDisplayName = c.FromDisplayName,
                    ToEveryone = c.ToEveryone,
                });
                outcome = sent ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = sent ? "pesan diteruskan" : "RetrivaClient tidak berjalan";
                break;
            }

            case AcknowledgeHelpCommand c:
            {
                var sent = await _session.SendAsync(new HelpAcknowledged
                {
                    CommandId = command.CommandId,
                    By = c.IssuedBy,
                });
                outcome = sent ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = sent ? "permintaan bantuan ditandai ditangani" : "RetrivaClient tidak berjalan";
                break;
            }

            case PushFileCommand c:
            {
                // Unduhan berjalan di task terpisah dan perintahnya dibalas
                // segera. Menunggu berkas 500 MB selesai di dalam handler ini
                // akan menahan seluruh antrean perintah unit tersebut — kunci
                // layar dan siaran ikut tertunda berpuluh detik.
                _ = Task.Run(() => ReceiveFileAsync(c), CancellationToken.None);
                outcome = CommandOutcome.Success;
                detail = $"mengunduh {c.FileName}";
                break;
            }

            case UpdateLabCommand c:
            {
                log.LogInformation("Perintah pembaruan diterima: target versi {Version}", c.TargetVersion);
                _ = Task.Run(() => ApplySelfUpdateAsync(c), CancellationToken.None);
                outcome = CommandOutcome.Success;
                detail = $"memulai pembaruan ke versi {c.TargetVersion}";
                break;
            }

            case ClearChatCommand:
            {
                var sent = await _session.SendAsync(new ClearChat
                {
                    CommandId = command.CommandId,
                });
                outcome = sent ? CommandOutcome.Success : CommandOutcome.Failed;
                detail = sent ? "percakapan dikosongkan" : "RetrivaClient tidak berjalan";
                break;
            }

            case InjectInputCommand c:
            {
                // Peristiwa input TIDAK dilaporkan balik ke konsol.
                //
                // Gerakan mouse datang puluhan kali per detik. Melaporkan tiap
                // satu berarti puluhan pesan per detik per unit mengalir kembali
                // ke hub hanya untuk menyatakan hal yang sudah jelas, dan itu
                // akan menenggelamkan jalur yang dipakai perintah sungguhan.
                await _session.SendAsync(new InjectInput
                {
                    CommandId = command.CommandId,
                    Kind = MapInputKind(c.Kind),
                    X = c.X,
                    Y = c.Y,
                    KeyCode = c.KeyCode,
                    WheelDelta = c.WheelDelta,
                    Button = c.Button,
                });
                return;
            }

            default:
                detail = $"{command.GetType().Name} belum diimplementasikan";
                break;
        }

        if (_connection is { State: HubConnectionState.Connected })
        {
            await _connection.SendAsync("ReportCommandResult", new CommandResult
            {
                CommandId = command.CommandId,
                UnitId = _unitId,
                Outcome = outcome,
                Detail = detail,
            });
        }
    }

    private static string? Arg(string name)
    {
        var argv = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(argv, name);
        return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
    }

    /// <summary>
    /// Perintah yang dieksekusi klien dibalas belakangan, bukan seketika.
    /// Pemetaan ini menjaga agar balasan ke konsol memakai CommandId asli.
    /// </summary>
    private void RememberAwaitingAck(Guid consoleCommandId) =>
        RememberAwaitingAck(consoleCommandId, consoleCommandId);

    private void RememberAwaitingAck(Guid clientCommandId, Guid consoleCommandId)
    {
        lock (_awaitingClientAck) _awaitingClientAck[clientCommandId] = consoleCommandId;
    }

    /// <summary>
    /// Mengunduh satu berkas dan melaporkan kemajuannya.
    ///
    /// Laporan dibatasi paling cepat sekali per 400 ms dan hanya bila persennya
    /// berubah. Tanpa pembatasan itu, berkas 500 MB dengan buffer 128 KB akan
    /// memancarkan sekitar empat ribu laporan per unit — dikali 40 unit, hub
    /// tenggelam oleh kabar kemajuan dan perintah sungguhan ikut tertunda.
    /// </summary>
    private async Task ReceiveFileAsync(PushFileCommand command)
    {
        var downloader = new FileDownloader(_config, log, _session.UserDocumentsDirectory, _activeThumbprint, _unitId);

        var lastReport = 0L;
        var lastPercent = -1;

        void Report(string status, long received, string? detail = null)
        {
            if (_connection is not { State: HubConnectionState.Connected }) return;
            _ = _connection.SendAsync("ReportFileProgress", new FileTransferProgress
            {
                UnitId = _unitId,
                TransferId = command.TransferId,
                FileName = command.FileName,
                BytesReceived = received,
                SizeBytes = command.SizeBytes,
                Status = status,
                Detail = detail,
            });
        }

        Report("mengunduh", 0);

        // Alamat lengkap disusun di sini, dari endpoint yang benar-benar dipakai.
        var url = command.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? command.DownloadUrl
            : $"https://{_consoleHost}:{_config.ConsolePort}{command.DownloadUrl}";

        var result = await downloader.DownloadAsync(
            url, command.FileName, command.SizeBytes, command.Sha256,
            received =>
            {
                var now = Environment.TickCount64;
                var percent = command.SizeBytes <= 0 ? 0 : (int)(received * 100 / command.SizeBytes);
                if (percent == lastPercent || now - lastReport < 400) return;
                lastReport = now;
                lastPercent = percent;
                Report("mengunduh", received);
            },
            CancellationToken.None);

        Report(result.Success ? "selesai" : "gagal",
               result.Success ? command.SizeBytes : 0, result.Detail);

        // Klien memberi tahu mahasiswa hanya bila berkasnya benar-benar utuh.
        // Memberi tahu lebih awal berarti mahasiswa membuka berkas separuh.
        if (result.Success && result.SavedPath is not null)
        {
            await _session.SendAsync(new FileArrived
            {
                FileName = command.FileName,
                SavedPath = result.SavedPath,
                SizeBytes = command.SizeBytes,
            });
        }
    }

    /// <summary>
    /// Mengunduh dan memasang paket pembaruan baru secara senyap di latar belakang.
    /// Installer akan menghentikan service, menimpa file di Program Files, dan menjalankan kembali service.
    /// </summary>
    private async Task ApplySelfUpdateAsync(UpdateLabCommand command)
    {
        try
        {
            var url = command.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? command.DownloadUrl
                : $"https://{_consoleHost}:{_config.ConsolePort}{command.DownloadUrl}";

            log.LogInformation("Mengunduh pembaruan ke versi {Version} dari {Url}...", command.TargetVersion, url);

            var tempInstaller = UpdateService.GetStagingPath(command.TargetVersion);
            await UpdateService.DownloadInstallerAsync(url, tempInstaller, null, CancellationToken.None);

            log.LogInformation("Pembaruan berhasil diunduh ke {Path}. Menjalankan pemasangan senyap...", tempInstaller);
            UpdateService.ApplySilentInstall(tempInstaller, "lab");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Gagal memasang pembaruan otomatis ke versi {Version}", command.TargetVersion);
        }
    }

    /// <summary>
    /// Loop berkala untuk memeriksa rilis baru di GitHub Releases secara mandiri.
    /// PC lab tidak perlu terhubung ke Superadmin untuk memperbarui dirinya.
    /// Ketika pembaruan sudah terunduh dan unit sedang idle (tidak ada siaran/remote),
    /// agent mengeksekusi pemasangan senyap secara otomatis di latar belakang.
    /// </summary>
    private async Task PeriodicUpdateCheckerAsync(CancellationToken stoppingToken)
    {
        // Beri jeda 30 detik setelah service mulai agar inisialisasi jaringan selesai
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var update = await UpdateService.CheckGitHubReleaseAsync(cancellationToken: stoppingToken);
                if (update != null)
                {
                    log.LogInformation("Pembaruan Retriva v{Version} tersedia di GitHub Releases. Mengunduh di latar belakang...", update.Version);
                    var installer = await UpdateService.DownloadUpdateInBackgroundAsync(update, cancellationToken: stoppingToken);

                    if (!string.IsNullOrWhiteSpace(installer) && File.Exists(installer))
                    {
                        log.LogInformation("Pembaruan v{Version} siap dipasang. Menunggu keadaan idle...", update.Version);

                        // Tunggu sampai mesin tidak sedang dipakai (tidak terkunci, tidak sedang diremote)
                        while (!stoppingToken.IsCancellationRequested)
                        {
                            var isBusy = _lockActive || _activeRemote;
                            if (!isBusy)
                            {
                                log.LogInformation("Unit dalam keadaan idle. Menjalankan pemasangan pembaruan senyap ke versi {Version}...", update.Version);
                                UpdateService.ApplySilentInstall(installer, "lab");
                                return; // Service akan dihentikan dan dimulai ulang oleh installer
                            }

                            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogDebug(ex, "Pengecekan pembaruan otomatis berkala dilewati");
            }

            // Periksa kembali setiap 2 jam
            await Task.Delay(TimeSpan.FromHours(2), stoppingToken);
        }
    }

    /// <summary>Melapor ke konsol di luar siklus perintah, mis. saat terjadi
    /// sesuatu yang tidak diminta siapa pun.</summary>
    private void ReportToConsole(CommandOutcome outcome, string detail)
    {
        if (_connection is not { State: HubConnectionState.Connected }) return;

        _ = _connection.SendAsync("ReportCommandResult", new CommandResult
        {
            CommandId = Guid.NewGuid(),
            UnitId = _unitId,
            Outcome = outcome,
            Detail = detail,
        });
    }

    private void OnClientAck(ClientAck ack)
    {
        Guid consoleCommandId;
        lock (_awaitingClientAck)
        {
            if (!_awaitingClientAck.Remove(ack.CommandId, out consoleCommandId))
            {
                // Pesan bukan perintah yang ditunggu balasannya oleh konsol
                return;
            }
        }

        if (_connection is not { State: HubConnectionState.Connected }) return;

        _ = _connection.SendAsync("ReportCommandResult", new CommandResult
        {
            CommandId = consoleCommandId,
            UnitId = _unitId,
            Outcome = ack.Success ? CommandOutcome.Success : CommandOutcome.Failed,
            Detail = ack.Detail,
        });
    }

    /// <summary>
    /// Alamat lokal yang dipakai untuk menjangkau KOORDINATOR.
    ///
    /// BUKAN alamat yang dipakai untuk menjangkau internet.
    ///
    /// Versi sebelumnya menyelidik ke 8.8.8.8, yang memilih antarmuka menurut
    /// rute bawaan — yaitu jalan ke internet. Di PC lab yang punya kabel ke
    /// jaringan lab DAN Wi-Fi ke jaringan kampus, rute bawaan hampir selalu
    /// Wi-Fi, karena jaringan lab yang tertutup tidak punya jalan ke luar.
    ///
    /// Akibatnya agent melaporkan alamat dan MAC kartu Wi-Fi, sementara ia
    /// benar-benar berbicara dengan koordinator lewat kabel. Tidak ada galat:
    /// unit terdaftar, tampil online, dan menjalankan setiap perintah. Yang
    /// salah diam-diam adalah dua hal yang baru terasa jauh kemudian —
    /// penomoran unit memakai subnet yang keliru, dan Wake-on-LAN dialamatkan
    /// ke kartu nirkabel, yang hampir tidak pernah membangunkan PC.
    ///
    /// Menyelidik ke alamat koordinator memberi jawaban yang benar menurut
    /// definisi: itulah antarmuka yang membawa percakapan yang sesungguhnya.
    /// Connect pada soket UDP tidak mengirim satu paket pun — ia hanya menyuruh
    /// sistem memilih rute — jadi ini tidak menuntut koordinator sedang hidup.
    /// </summary>
    private static string LocalIpAddress(string consoleHost)
    {
        foreach (var target in new[] { consoleHost, "8.8.8.8" })
        {
            if (string.IsNullOrWhiteSpace(target)) continue;

            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(target, 65530);

                if (((IPEndPoint?)probe.LocalEndPoint)?.Address is { } local
                    && !IPAddress.IsLoopback(local))
                {
                    return local.ToString();
                }
            }
            catch (SocketException)
            {
                // Alamat koordinator tidak dapat dirutekan dari sini. Jatuh ke
                // penyelidikan internet, yang setidaknya memberi antarmuka yang
                // hidup — dan lebih baik daripada 0.0.0.0 yang tidak berarti apa pun.
            }
        }

        return "0.0.0.0";
    }

    /// <summary>
    /// Kartu jaringan yang BENAR-BENAR membawa alamat lab, bukan yang pertama
    /// kebetulan menyala.
    ///
    /// PrimaryMacAddress sebelumnya mengambil kartu pertama yang menyala dan
    /// bukan loopback. Di mesin bersih itu benar. Di mesin yang punya VirtualBox,
    /// Hyper-V, VPN, atau Bluetooth PAN — dan PC lab lambat laun punya salah
    /// satunya — kartu pertama bisa saja bukan kartu yang tersambung ke lab.
    ///
    /// MAC yang salah tidak menimbulkan galat apa pun. Unit terdaftar dengan
    /// wajar, tampil online, dan menjalankan setiap perintah dengan benar. Yang
    /// gagal hanya Wake-on-LAN, karena paket ajaibnya dialamatkan ke kartu yang
    /// tidak akan pernah membangunkan PC itu — dan kegagalan itu muncul pada
    /// pagi hari ketika seluruh lab seharusnya menyala sendiri.
    ///
    /// Kartu dicari lewat alamat IP yang sedang dipakai menjangkau jaringan.
    /// Itu bukan tebakan yang lebih baik, melainkan jawaban: kartu itu yang
    /// membawa paketnya keluar.
    /// </summary>
    private static NetworkInterface? ActiveNic(string localIp)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.ToString() == localIp) return nic;
                }
            }
        }
        catch (NetworkInformationException) { /* jatuh ke cadangan di bawah */ }

        // Cadangan: perilaku lama. Lebih baik daripada tidak melaporkan apa pun,
        // tetapi cukup buruk untuk dicatat supaya terlihat saat ditelusuri.
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                              && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
    }

    /// <summary>Keterangan tautan jaringan unit ini, sebagaimana diukur di unit.</summary>
    private static LinkReport DescribeLink(string localIp)
    {
        var nic = ActiveNic(localIp);
        if (nic is null) return new LinkReport { Mac = "000000000000" };

        var wireless = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211;

        return new LinkReport
        {
            Mac = nic.GetPhysicalAddress().ToString(),
            Wired = !wireless,
            Adapter = nic.Description,

            // Speed dilaporkan dalam bit per detik dan bernilai -1 bila tidak
            // diketahui. Dibagi tanpa memeriksa, "tidak diketahui" berubah
            // menjadi angka negatif yang tampak seperti hasil pengukuran.
            SpeedMbps = nic.Speed > 0 ? (int)(nic.Speed / 1_000_000) : 0,
        };
    }

    private sealed record LinkReport
    {
        public string Mac { get; init; } = "000000000000";
        public bool Wired { get; init; }
        public string Adapter { get; init; } = "";
        public int SpeedMbps { get; init; }
    }
}
