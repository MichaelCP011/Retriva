using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using Retriva.Agent.Power;
using Retriva.Agent.Session;
using Retriva.Shared.Local;
using Retriva.Shared;
using Retriva.Shared.Broadcast;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;
using Retriva.Shared.Security;

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
            FileInbox = FileDownloader.ResolveInbox(_config.FileInboxDirectory, log),
        };
        _session.AckReceived += OnClientAck;

        // Diteruskan sebagai laporan tanpa perintah, karena memang tidak ada
        // perintah yang memicunya. Tanpa ini panel admin akan terus menampilkan
        // sesi yang sudah mati — mahasiswa menekan pintasan keluar, aliran layar
        // berhenti, dan admin hanya melihat frame terakhir yang membeku.
        _session.RemoteEndedReceived += reason =>
            ReportToConsole(CommandOutcome.Success, $"kendali.berakhir: {reason}");

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

            if (!connected && _lockActive)
            {
                _lockActive = false;
                log.LogWarning("Klien menghilang saat layar terkunci — kemungkinan ditutup paksa");
                ReportToConsole(CommandOutcome.Failed,
                    "klien ditutup saat layar terkunci — kemungkinan lewat Task Manager");
            }
        };
        _session.Start(_unitId, () => _connection?.State == HubConnectionState.Connected);

        if (string.IsNullOrWhiteSpace(_config.ConsoleCertificateThumbprint))
        {
            log.LogError("Sidik jari sertifikat konsol belum diatur di {File}. "
                       + "Agent menolak menyambung tanpa penyematan.", RetrivaPaths.AgentConfigFile);
            return;
        }

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var connected = false;

            foreach (var endpoint in _config.ConsoleEndpoints)
            {
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    await RunAgainstAsync(endpoint, stoppingToken);
                    connected = true;
                    attempt = 0;
                    break;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    /*
                        SELURUH RANTAI INNER EXCEPTION DICATAT, BUKAN HANYA
                        PESAN TERLUARNYA.

                        Kegagalan TLS selalu muncul sebagai "The SSL connection
                        could not be established, see inner exception." — kalimat
                        yang secara harfiah menyuruh melihat sesuatu yang tidak
                        pernah ikut dicetak. Sebab sesungguhnya ada satu atau dua
                        lapis di dalamnya: sidik jari yang tidak cocok, tidak ada
                        yang mendengarkan di port itu, atau sertifikat kedaluwarsa.
                        Ketiganya menuntut perbaikan yang sepenuhnya berbeda, dan
                        dari pesan terluarnya ketiganya terlihat sama persis.
                    */
                    log.LogWarning("Gagal terhubung ke {Endpoint}: {Chain}",
                        endpoint, DescribeChain(ex));
                }
            }

            if (!connected) attempt++;

            /*
                SETELAH BEBERAPA KALI GAGAL, ALAMATNYA DICARI ULANG.

                Alamat koordinator ditulis sekali saat pemasangan dan tidak
                pernah diperbarui sesudahnya. DHCP yang memberi PC Asisten
                alamat lain setelah pemadaman listrik, router yang diganti, atau
                lab yang pindah ruangan — semuanya membuat empat puluh unit diam
                serentak, dengan layanan yang berjalan sempurna dan tanpa satu
                galat pun yang sampai ke siapa pun.

                Perbaikan yang tersedia sebelum ini hanyalah memasang ulang
                keempat puluhnya.

                Dicoba pada percobaan ketiga, bukan yang pertama. Koordinator
                yang sedang dimuat ulang atau mesin yang baru bangun wajar
                menolak satu-dua kali, dan mendengarkan jaringan pada setiap
                kegagalan berarti empat puluh unit menyiram multicast tiap
                beberapa detik pada gangguan yang akan pulih sendiri.

                Yang diterima HANYA lab dengan sidik jari yang sama. Alamatnya
                diperbaiki sendiri; berpindah lab tetap menuntut kode
                pendaftaran lab tujuan. Lihat catatan panjang di LabRediscovery.
            */
            if (!connected && attempt >= 3)
            {
                var found = await Task.Run(
                    () => LabRediscovery.FindTrusted(
                        _config.ConsoleCertificateThumbprint, TimeSpan.FromSeconds(6), log),
                    stoppingToken);

                if (found is not null && !_config.ConsoleEndpoints.Contains(found))
                {
                    log.LogWarning("Alamat koordinator berubah — memakai {Endpoint}", found);

                    // Ditaruh di DEPAN, alamat lama dipertahankan di belakangnya.
                    // Lab yang sempat berpindah alamat lalu kembali ke alamat
                    // semula tidak perlu ditemukan ulang dari awal.
                    _config.ConsoleEndpoints.Insert(0, found);

                    // Disimpan ke berkas, bukan hanya dipegang di memori.
                    // Tanpa ini, layanan yang di-restart atau PC yang dimatikan
                    // sore hari akan kembali ke alamat lama esok paginya dan
                    // mengulang seluruh pencarian — pada empat puluh unit
                    // sekaligus, tiap pagi.
                    try
                    {
                        ConfigStore.Save(RetrivaPaths.AgentConfigFile, _config);
                        log.LogInformation("Alamat baru disimpan ke {File}", RetrivaPaths.AgentConfigFile);
                    }
                    catch (IOException ex)
                    {
                        log.LogWarning("Alamat baru tidak dapat disimpan ({Message}) — "
                                     + "dipakai untuk sesi ini saja", ex.Message);
                    }

                    attempt = 0;
                    continue;
                }
            }

            var delay = JitteredRetryPolicy.EndpointRetryDelay(attempt);
            log.LogInformation("Mencoba lagi dalam {Seconds:F0} detik", delay.TotalSeconds);
            await Task.Delay(delay, stoppingToken);
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
    /// benar-benar sampai. Jadi agent yang mengisinya, bukan admin.
    /// </summary>
    private string? _consoleHost;

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

    private async Task RunAgainstAsync(string endpoint, CancellationToken ct)
    {
        _consoleHost = HostFromEndpoint(endpoint);
        _connection = ConsoleConnection.Build(endpoint, _config, RetrivaProtocol.HubPath);
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
            if (_connection.State == HubConnectionState.Connected)
            {
                try
                {
                    // Saat terkunci, kirim ulang frame sebelum penguncian.
                    var jpeg = _lockActive ? _lastThumbnail : ScreenThumbnail.Capture(log);
                    if (!_lockActive && jpeg.Length > 0) _lastThumbnail = jpeg;

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
            EnrollmentSecret = string.IsNullOrWhiteSpace(_config.DeviceToken) ? _config.EnrollmentSecret : null,
            DeviceToken = _config.DeviceToken,
        }, ct);

        if (!response.Accepted)
        {
            log.LogError("Pendaftaran ditolak: {Reason}", response.RejectReason);
            return false;
        }

        // Token diterbitkan sekali; setelah tersimpan, kode pendaftaran tidak dipakai lagi.
        if (!string.IsNullOrWhiteSpace(response.IssuedDeviceToken)
            && response.IssuedDeviceToken != _config.DeviceToken)
        {
            _config.DeviceToken = response.IssuedDeviceToken;
            ConfigStore.Save(RetrivaPaths.AgentConfigFile, _config);
            log.LogInformation("Token perangkat tersimpan");
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
                    Message = c.Message,
                    IssuedBy = c.IssuedBy,
                });
                log.LogInformation("Kunci diteruskan ke klien: {Sent} (klien tersambung: {Connected})",
                    sent, _session.ClientConnected);
                if (sent) { _lockActive = true; RememberAwaitingAck(command.CommandId); return; }
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case UnlockScreenCommand:
            {
                var sent = await _session.SendAsync(new HideLockScreen());
                if (sent) { _lockActive = false; RememberAwaitingAck(command.CommandId); return; }
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
                var sent = await _session.SendAsync(new EndExamWatch());
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
                var sent = await _session.SendAsync(new EndPresenting());
                if (sent) { RememberAwaitingAck(command.CommandId); return; }

                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan di sesi pengguna";
                break;
            }

            case StopBroadcastCommand:
            {
                var sent = await _session.SendAsync(new HideBroadcast());
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

                var sent = await _session.SendAsync(new BeginRemoteSession
                {
                    AdminHost = _consoleHost,
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
                var sent = await _session.SendAsync(new EndRemoteSession());
                if (sent) { RememberAwaitingAck(command.CommandId); return; }
                outcome = CommandOutcome.Failed;
                detail = "RetrivaClient tidak berjalan";
                break;
            }

            case SendChatCommand c:
            {
                var sent = await _session.SendAsync(new DeliverChat
                {
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
                var sent = await _session.SendAsync(new HelpAcknowledged { By = c.IssuedBy });
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

            case ClearChatCommand:
            {
                var sent = await _session.SendAsync(new ClearChat());
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
    private void RememberAwaitingAck(Guid consoleCommandId)
    {
        lock (_awaitingClientAck) _awaitingClientAck[consoleCommandId] = consoleCommandId;
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
        var downloader = new FileDownloader(_config, log);

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
            if (_awaitingClientAck.Count == 0) return;
            consoleCommandId = _awaitingClientAck.Keys.First();
            _awaitingClientAck.Remove(consoleCommandId);
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
