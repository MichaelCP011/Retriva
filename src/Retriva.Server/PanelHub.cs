using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Retriva.Server.Data;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Titik masuk seluruh komunikasi dari panel admin.
///
/// Terpisah dari AgentHub karena arah kuasanya berlawanan: agent adalah mesin
/// yang dikendalikan, panel adalah orang yang mengendalikan. Menyatukan keduanya
/// berarti setiap metode harus memeriksa "ini agent atau panel" sebelum berbuat
/// apa pun, dan satu pemeriksaan yang terlewat membuka seluruh lab kepada
/// siapa pun yang bisa menyamar sebagai agent.
/// </summary>
public sealed class PanelHub(
    PanelRegistry panels,
    PanelEnrollment enrollment,
    UnitRegistry registry,
    UnitStore units,
    ExamStore exams,
    ActivityLog activity,
    ChatStore chats,
    BatchTracker batches,
    ConsoleConfig config,
    CoordinatorIdentity identity,
    BroadcastRelay relay,
    IHubContext<AgentHub> agents,
    ILogger<PanelHub> log) : Hub
{
    /* ================= sambungan ================= */

    public Task<PanelWelcome> RegisterPanel(PanelHello hello)
    {
        /*
            Apakah sambungan ini berasal dari mesin yang sama ditentukan dari
            alamat remote milik koneksi, BUKAN dari apa pun yang dikirim panel.

            Kalau panel yang menyatakannya sendiri, siapa pun di jaringan lab
            tinggal mengirim "saya lokal" dan langsung menjadi Superadmin. Ini
            satu-satunya tempat pemeriksaan itu boleh dilakukan.
        */
        var remote = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        var fromLoopback = IsLoopback(remote);

        var welcome = panels.Register(
            hello, Context.ConnectionId, fromLoopback,
            enrollment, identity.Thumbprint, config.LabName);

        log.LogInformation(
            "Panel {Name} ({Device}) dari {Remote}: {Result} sebagai {Role}",
            hello.DisplayName, hello.DeviceId, remote,
            welcome.Accepted ? "diterima" : "ditolak", welcome.Role);

        if (welcome.Accepted && welcome.Role == PanelRole.Pending)
        {
            activity.Write("system", "panel.pending", detail:
                $"{hello.DisplayName} menunggu persetujuan Superadmin");
        }

        return Task.FromResult(welcome);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        panels.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Apakah alamat ini berasal dari mesin yang sama.
    ///
    /// IPAddress.IsLoopback SAJA TIDAK CUKUP, dan inilah sebabnya.
    ///
    /// Kestrel mendengarkan lewat ListenAnyIP, yang membuka soket IPv6
    /// dual-stack. Sambungan ke 127.0.0.1 tiba di soket itu sebagai alamat
    /// IPv4 yang dipetakan ke IPv6: ::ffff:127.0.0.1. Bentuk itu bukan ::1,
    /// dan IsLoopback hanya mengenali ::1 untuk keluarga IPv6 — jadi ia
    /// menjawab false untuk sambungan yang jelas-jelas dari mesin yang sama.
    ///
    /// Akibatnya panel milik Superadmin sendiri diperlakukan sebagai panel
    /// jaringan: masuk sebagai Pending, kehilangan seluruh wewenang, dan
    /// menampilkan "menunggu persetujuan" — menunggu dirinya sendiri, yang
    /// tidak akan pernah datang karena tidak ada lagi yang berwenang menyetujui.
    ///
    /// Alamatnya dipetakan kembali ke IPv4 sebelum diperiksa.
    /// </summary>
    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(normalized);
    }

    /* ================= wewenang ================= */

    /// <summary>
    /// Memeriksa wewenang pemanggil.
    ///
    /// Dipanggil di SETIAP metode yang berbuat sesuatu, tanpa kecuali. Panel
    /// menyembunyikan tombol yang tidak boleh dipakai, tetapi itu hanya
    /// kenyamanan — pesan hub dapat disusun sendiri oleh siapa pun, jadi
    /// penyembunyian tombol tidak pernah menjadi pengaman.
    /// </summary>
    private PanelState? Authorize(PanelRights needed, out string reason)
    {
        var panel = panels.ByConnection(Context.ConnectionId);

        if (panel is null)
        {
            reason = "Panel belum terdaftar pada sambungan ini.";
            return null;
        }

        if (panel.Role == PanelRole.Pending)
        {
            reason = "Panel ini menunggu persetujuan Superadmin.";
            return null;
        }

        if ((PanelRegistry.RightsFor(panel.Role) & needed) != needed)
        {
            reason = $"Peran {panel.Role} tidak berwenang melakukan tindakan ini.";
            return null;
        }

        reason = "";
        return panel;
    }

    /* ================= perintah ================= */

    /// <summary>
    /// Satu pintu untuk seluruh perintah ke agent.
    ///
    /// Pencarian connection id, pelacakan batch, dan pencatatan audit dikerjakan
    /// di sini — sekali, bukan sepuluh kali di sepuluh tempat panggilan seperti
    /// sebelumnya. Yang paling sering terlewat dari ketiganya adalah auditnya,
    /// dan justru itu yang dicari orang setelah sesuatu terlanjur terjadi.
    /// </summary>
    public Task<PanelDispatchResult> Dispatch(PanelDispatch request)
    {
        var needed = RightsForAction(request.Command);
        var panel = Authorize(needed, out var reason);

        if (panel is null)
        {
            log.LogWarning("Dispatch {Action} ditolak: {Reason}", request.Action, reason);
            return Task.FromResult(new PanelDispatchResult
            {
                Accepted = false,
                RejectReason = reason,
            });
        }

        var online = registry.Snapshot().Where(u => u.Status != UnitStatus.Offline);

        var targets = (request.Targets is { Length: > 0 }
                ? online.Where(u => request.Targets.Contains(u.UnitId))
                : online)
            .Select(u => u.UnitId)
            .ToList();

        if (targets.Count == 0)
        {
            return Task.FromResult(new PanelDispatchResult
            {
                Accepted = false,
                RejectReason = "Tidak ada unit tujuan yang tersambung.",
            });
        }

        var connections = targets
            .Select(registry.ConnectionIdFor)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        // Selisih antara yang dilacak dan yang benar-benar terjangkau berarti
        // ada unit yang dihitung online padahal koneksinya sudah tidak ada.
        // Tanpa baris ini, perintahnya sekadar menghilang tanpa jejak.
        if (connections.Count != targets.Count)
        {
            log.LogWarning("{Action}: {Tracked} unit dilacak, {Reachable} terjangkau",
                request.Action, targets.Count, connections.Count);
        }

        /*
            IssuedBy DITIMPA di sini, tidak dipakai apa adanya dari panel.

            Nilai yang dikirim panel tidak dibuktikan oleh apa pun — panel yang
            diubah bisa menuliskan nama siapa saja di situ. Karena inilah nama
            yang muncul di layar mahasiswa dan tercatat di jejak audit, memakai
            nilai kirimannya berarti jejak audit dapat dikarang oleh orang yang
            justru sedang diawasi olehnya.

            Nama yang dipakai berasal dari pendaftaran panel yang sudah lolos
            pemeriksaan token, jadi yang tercatat adalah panel yang benar-benar
            mengirim, bukan panel yang mengaku mengirim.
        */
        var command = request.Command with { IssuedBy = panel.DisplayName };

        /*
            Daftar unit layar penuh diisi KOORDINATOR, bukan panel.

            Panel tidak tahu unit mana yang tercolok proyektor — itu setelan lab
            yang tersimpan di basis data koordinator. Membiarkan panel yang
            mengisinya berarti setiap panel harus menyalin setelan itu, dan yang
            lupa memperbaruinya akan membuka layar penuh di PC yang salah.
        */
        if (command is StartBroadcastCommand start)
            command = start with { FullscreenUnits = [.. units.AutoFullscreen()] };

        /*
            Alamat relay untuk presentasi mahasiswa DITIMPA per unit tujuan.

            Panel menerima alamat relay saat mengklaim siaran, dan alamat itu
            benar — untuk panel. Tetapi yang akan mengirim layar di sini bukan
            panel, melainkan PC mahasiswa, dan ia belum tentu menjangkau
            koordinator lewat alamat yang sama. Koordinator dengan beberapa
            kartu jaringan punya beberapa alamat; laptop dosen di Wi-Fi dan PC
            mahasiswa di kabel dapat sampai lewat dua alamat berbeda.

            Diuji di satu mesin, keduanya kebetulan sama dan salahnya tidak
            pernah muncul. Di lab, unit akan mengirim ke alamat yang tidak
            terjangkau, relay tidak menerima satu paket pun, dan tak seorang pun
            mendapat pesan kesalahan — hanya layar hitam di seluruh kelas.

            Yang dipakai di sini adalah alamat yang dicatat saat unit itu
            mendaftar: alamat yang terbukti sampai, karena unit itu sendiri baru
            saja memakainya.
        */
        if (command is StartPresentingCommand present && targets.Count == 1
            && registry.CoordinatorHostFor(targets[0]) is { } reachable
            && !string.Equals(reachable, present.RelayHost, StringComparison.Ordinal))
        {
            log.LogInformation(
                "Alamat relay untuk {Unit} disesuaikan: {Panel} → {Unit_}",
                targets[0], present.RelayHost, reachable);
            command = present with { RelayHost = reachable };
        }

        batches.Begin(command.CommandId, request.Action, targets, TimeSpan.FromSeconds(12));
        FireToAgents(agents.Clients.Clients(connections).SendAsync("Execute", command), request.Action);

        activity.Write(panel.DisplayName, request.Action,
            detail: $"disebar ke {connections.Count} unit");

        return Task.FromResult(new PanelDispatchResult
        {
            Accepted = true,
            Reached = connections.Count,
            BatchId = command.CommandId,
        });
    }

    /// <summary>
    /// Memetakan perintah ke wewenang yang dibutuhkannya.
    ///
    /// Ditulis sebagai daftar eksplisit, bukan diturunkan dari nama tipe.
    /// Perintah baru yang tidak tercantum di sini jatuh ke ManageCredentials —
    /// wewenang tertinggi — sehingga kelalaian mencantumkannya berakibat
    /// perintah itu DITOLAK, bukan diizinkan diam-diam.
    /// </summary>
    private static PanelRights RightsForAction(AgentCommand command) => command switch
    {
        PingCommand => PanelRights.Chat,

        LockScreenCommand or UnlockScreenCommand => PanelRights.LockScreen,

        ShutdownCommand or RestartCommand or AbortShutdownCommand => PanelRights.PowerControl,

        StartBroadcastCommand or StopBroadcastCommand
            or FreezeBroadcastCommand => PanelRights.Broadcast,

        // Menyuruh mahasiswa menyiarkan layarnya menuntut wewenang yang sama
        // dengan menyiarkan layar sendiri: keduanya menentukan apa yang dilihat
        // seluruh kelas.
        StartPresentingCommand or StopPresentingCommand => PanelRights.Broadcast,

        BeginRemoteCommand or EndRemoteCommand
            or InjectInputCommand => PanelRights.RemoteControl,

        SendChatCommand or AcknowledgeHelpCommand or ClearChatCommand => PanelRights.Chat,

        PushFileCommand => PanelRights.FileShare,

        _ => PanelRights.ManageCredentials,
    };

    /// <summary>
    /// Meneruskan satu peristiwa mouse atau keyboard ke unit yang dikendalikan.
    ///
    /// SENGAJA TIDAK LEWAT Dispatch.
    ///
    /// Gerakan mouse datang puluhan kali per detik. Melewatkannya ke Dispatch
    /// berarti satu baris audit dan satu batch per gerakan — basis data penuh
    /// derau dalam hitungan menit, dan peristiwa yang benar-benar perlu
    /// diaudit terkubur di bawahnya. Yang dicatat adalah pembukaan sesi
    /// kendalinya, bukan tiap piksel yang dilalui kursor.
    /// </summary>
    public Task RelayInput(string unitId, InjectInputCommand command)
    {
        var panel = Authorize(PanelRights.RemoteControl, out _);
        if (panel is null) return Task.CompletedTask;

        if (registry.ConnectionIdFor(unitId) is { } connectionId)
        {
            _ = agents.Clients.Client(connectionId)
                      .SendAsync("Execute", (AgentCommand)(command with { IssuedBy = panel.DisplayName }));
        }

        return Task.CompletedTask;
    }

    /* ================= chat ================= */

    /// <summary>
    /// Mengirim pesan ke satu unit, atau pengumuman ke seluruh unit online.
    ///
    /// Penyimpanan riwayat dan pengiriman perintah dikerjakan bersama di sini.
    /// Kalau panel yang melakukannya dalam dua panggilan terpisah, panel yang
    /// putus di antara keduanya akan meninggalkan pesan yang tersimpan tetapi
    /// tidak pernah sampai — atau sampai tetapi tidak tercatat.
    /// </summary>
    public Task<ChatSendResult> SendChat(string? unitId, string text, bool toEveryone)
    {
        var panel = Authorize(PanelRights.Chat, out var reason);
        if (panel is null) return Task.FromResult(new ChatSendResult(false, 0, reason));

        var body = text.Trim();
        if (body.Length == 0) return Task.FromResult(new ChatSendResult(false, 0, "Pesan kosong."));

        var sender = panel.DisplayName;
        var command = new SendChatCommand
        {
            IssuedBy = sender,
            Text = body,
            FromDisplayName = sender,
            ToEveryone = toEveryone,
        };

        if (toEveryone)
        {
            var online = registry.Snapshot().Where(u => u.Status != UnitStatus.Offline).ToList();
            if (online.Count == 0)
                return Task.FromResult(new ChatSendResult(false, 0, "Tidak ada unit tersambung."));

            // Pengumuman dicatat untuk SETIAP unit yang menerimanya, bukan
            // sekali secara global. Dicatat sekali, riwayat satu mahasiswa akan
            // berlubang: jawaban yang merujuk pengumuman muncul tanpa
            // pengumumannya, dan percakapannya jadi tidak masuk akal dibaca.
            foreach (var unit in online)
            {
                var stored = new ChatMessage
                {
                    UnitId = unit.UnitId,
                    FromAdmin = true,
                    DisplayName = sender,
                    Text = body,
                };

                chats.Append(stored);

                /*
                    Diedarkan ke SELURUH panel, termasuk yang mengirimnya.

                    Sebelumnya pesan admin hanya disimpan lalu diteruskan ke
                    agent. Tidak ada panel yang pernah diberi tahu — panel
                    pengirim memalsukannya dengan menggambar sendiri, dan
                    pengumuman tidak pernah muncul di percakapan siapa pun.

                    Akibatnya: asisten mengirim pengumuman, layarnya tidak
                    berubah, lalu ia mengirimnya lagi. Dan panel dosen tidak
                    pernah tahu asisten sudah menjawab mahasiswa yang sama.

                    Satu jalur untuk kedua arah berarti tidak ada lagi versi
                    percakapan yang hanya benar di satu jendela.
                */
                AgentHub.RaiseChatArrived(stored);
            }

            var connections = online
                .Select(u => registry.ConnectionIdFor(u.UnitId))
                .Where(id => id is not null)
                .Select(id => id!)
                .ToList();

            FireToAgents(agents.Clients.Clients(connections).SendAsync("Execute", (AgentCommand)command), "chat.broadcast");
            activity.Write(sender, "chat.broadcast", detail: $"pengumuman ke {connections.Count} unit");
            return Task.FromResult(new ChatSendResult(true, connections.Count, null));
        }

        if (unitId is null) return Task.FromResult(new ChatSendResult(false, 0, "Unit tujuan kosong."));

        if (registry.ConnectionIdFor(unitId) is not { } single)
            return Task.FromResult(new ChatSendResult(false, 0, $"{unitId} sedang tidak tersambung."));

        var message = new ChatMessage
        {
            UnitId = unitId,
            FromAdmin = true,
            DisplayName = sender,
            Text = body,
        };

        chats.Append(message);
        AgentHub.RaiseChatArrived(message);

        FireToAgents(agents.Clients.Client(single).SendAsync("Execute", (AgentCommand)command), $"chat ke {unitId}");
        return Task.FromResult(new ChatSendResult(true, 1, null));
    }

    /// <summary>
    /// Menghapus seluruh percakapan, di Superadmin maupun di layar mahasiswa.
    ///
    /// Perintah disebar ke SELURUH unit, bukan hanya yang punya percakapan.
    /// Layar mahasiswa berikutnya adalah PC fisik yang sama; menghapus hanya
    /// di sisi Superadmin meninggalkan percakapan lama terpampang di tempat
    /// yang paling mudah dibaca orang yang tidak berhak — justru bahaya yang
    /// ingin dicegah dengan menghapusnya.
    /// </summary>
    public Task<int> ClearChat()
    {
        var panel = Authorize(PanelRights.Chat, out _);
        if (panel is null) return Task.FromResult(0);

        var removed = chats.ClearAll();
        registry.ClearAllHelp();

        var connections = registry.Snapshot()
            .Where(u => u.Status != UnitStatus.Offline)
            .Select(u => registry.ConnectionIdFor(u.UnitId))
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        _ = agents.Clients.Clients(connections)
                  .SendAsync("Execute", (AgentCommand)new ClearChatCommand { IssuedBy = panel.DisplayName });

        activity.Write(panel.DisplayName, "chat.cleared", detail: $"{removed} pesan dihapus");
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Menandai permintaan bantuan sudah ditangani.
    ///
    /// Klien ikut diberi tahu, bukan hanya penanda di panel yang dipadamkan.
    /// Mahasiswa perlu melihat permintaannya sampai; tanpa itu ia menekan
    /// tombolnya lagi karena tidak ada yang berubah di layarnya.
    /// </summary>
    public Task<bool> AcknowledgeHelp(string unitId)
    {
        var panel = Authorize(PanelRights.Chat, out _);
        if (panel is null) return Task.FromResult(false);

        registry.SetHelpRequested(unitId, false);
        activity.Write(panel.DisplayName, "help.acknowledged", unitId);

        if (registry.ConnectionIdFor(unitId) is { } connectionId)
        {
            _ = agents.Clients.Client(connectionId).SendAsync(
                "Execute", (AgentCommand)new AcknowledgeHelpCommand { IssuedBy = panel.DisplayName });
        }

        return Task.FromResult(true);
    }

    /* ================= daya ================= */

    /// <summary>
    /// Menyalakan unit lewat magic packet.
    ///
    /// Satu-satunya perintah yang tidak lewat agent: saat unit mati tidak ada
    /// agent yang berjalan untuk menerimanya. Dijalankan di Superadmin, bukan
    /// di panel, karena magic packet harus dipancarkan ke subnet lab — dan
    /// laptop dosen di Wi-Fi tamu belum tentu berada di subnet itu.
    /// </summary>
    public Task<int> WakeUnits()
    {
        var panel = Authorize(PanelRights.PowerControl, out _);
        if (panel is null) return Task.FromResult(0);

        var macs = units.MacAddresses();
        var sent = Power.WakeOnLan.SendMagicPackets(macs.Values, config.SubnetPrefix, log);
        activity.Write(panel.DisplayName, "power.wake", detail: $"magic packet ke {sent} unit");
        return Task.FromResult(sent);
    }

    /* ================= siaran ================= */

    /// <summary>
    /// Meminta hak menyiarkan, dan menerima alamat tujuan alirannya.
    ///
    /// Panel TIDAK memancarkan multicast sendiri. Ia mengirim satu aliran
    /// unicast ke alamat yang dikembalikan di sini, dan Superadmin yang
    /// memancarkannya ke lab — lihat BroadcastRelay untuk alasannya.
    /// </summary>
    public Task<BroadcastGrant> ClaimBroadcast(BroadcastClaim claim)
    {
        var panel = Authorize(PanelRights.Broadcast, out var reason);
        if (panel is null) return Task.FromResult(new BroadcastGrant
        {
            Granted = false,
            HeldBy = null,
            Warning = reason,
        });

        var (granted, heldBy) = relay.TryClaim(
            panel.DeviceId, panel.DisplayName, claim.SourceLabel, claim.Takeover);

        if (!granted)
        {
            return Task.FromResult(new BroadcastGrant
            {
                Granted = false,
                HeldBy = heldBy,
            });
        }

        activity.Write(panel.DisplayName, "broadcast.claim", detail: claim.SourceLabel);

        return Task.FromResult(new BroadcastGrant
        {
            Granted = true,

            // Alamat relay dilihat dari sudut pandang PANEL, yaitu alamat
            // tujuan yang ia pakai untuk menyambung ke hub ini. Superadmin
            // tidak dapat menebaknya sendiri: mesin dengan beberapa kartu
            // jaringan punya beberapa alamat, dan hanya panelnya yang tahu
            // lewat mana ia benar-benar sampai.
            RelayHost = Context.GetHttpContext()?.Connection.LocalIpAddress is { } local
                ? (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).ToString()
                : PanelProtocol.LoopbackHost,

            RelayPort = PanelProtocol.RelayPort,

            // Peringatan, bukan penolakan.
            //
            // Dosen yang mendapati kualitasnya diturunkan diam-diam akan
            // menyimpulkan aplikasinya rusak. Yang diberi tahu justru dapat
            // memutuskan sendiri: menyambungkan kabel, atau menerima gambar
            // yang lebih lembut.
            Warning = panel.WiredConnection
                ? null
                : "Panel ini tersambung lewat Wi-Fi. Siaran 1080p menuntut sekitar "
                + "45 Mbps ke atas — sambungkan kabel bila gambarnya tersendat.",
        });
    }

    public Task ReleaseBroadcast()
    {
        var panel = Authorize(PanelRights.Broadcast, out _);
        if (panel is null) return Task.CompletedTask;

        relay.Release(panel.DeviceId);
        activity.Write(panel.DisplayName, "broadcast.release");
        return Task.CompletedTask;
    }

    /* ================= jejak audit ================= */

    /// <summary>
    /// Mencatat tindakan yang seluruhnya terjadi di panel dan tidak menyentuh
    /// agent mana pun — membekukan siaran, menyimpan pengaturan, membuang
    /// berkas dari lemari. Tetap perlu tercatat: yang dicari orang setelah
    /// sesuatu terjadi adalah urutan kejadiannya, dan urutan yang bolong di
    /// bagian tengah tidak menjelaskan apa-apa.
    /// </summary>
    public Task WriteActivity(string action, string? unitId, string? detail)
    {
        var panel = Authorize(PanelRights.Chat, out _);
        if (panel is not null) activity.Write(panel.DisplayName, action, unitId, detail);
        return Task.CompletedTask;
    }

    /* ================= pembacaan ================= */

    public Task<UnitSnapshot[]> GetUnits()
    {
        if (Authorize(PanelRights.Chat, out _) is null) return Task.FromResult(Array.Empty<UnitSnapshot>());

        // Pemuatan pertama membawa pratinjau; dorongan berkala tidak.
        return Task.FromResult(UnitSnapshots.Build(registry, units, chats, includeThumbnails: true));
    }

    public Task<ActivityEntryDto[]> GetActivity(int limit)
    {
        if (Authorize(PanelRights.Chat, out _) is null) return Task.FromResult(Array.Empty<ActivityEntryDto>());

        return Task.FromResult(activity.Recent(Math.Clamp(limit, 1, 200))
            .Select(e => new ActivityEntryDto(e.UnitId, e.Actor, e.Action, e.Detail, e.CreatedAt))
            .ToArray());
    }

    public Task<ChatMessage[]> GetChatHistory(string unitId)
    {
        if (Authorize(PanelRights.Chat, out _) is null) return Task.FromResult(Array.Empty<ChatMessage>());

        chats.MarkRead(unitId);
        return Task.FromResult(chats.History(unitId).ToArray());
    }

    /* ================= pengelolaan unit ================= */

    /// <summary>
    /// Memberi nama pilihan pada sebuah unit.
    ///
    /// Alias dikunci ke unit id yang berasal dari token perangkat, bukan ke
    /// alamat IP. Alias yang dikunci ke IP akan berpindah orang begitu DHCP
    /// memberi alamat yang sama kepada laptop lain — dan asisten akan mengira
    /// ia sedang mengunci layar Bu Rina padahal itu laptop mahasiswa lain.
    /// </summary>
    /*
        BLOKIR TIDAK LAGI DAPAT DIPANGGIL DARI PANEL.

        Metodenya dihapus atas permintaan yang beralasan: unit yang menolak
        menyambung tanpa cara mendiagnosanya lebih mahal daripada masalah yang
        dicegahnya. Asisten melihat PC mati di grid, memeriksa kabel, memeriksa
        agent, dan tidak ada satu pun tempat yang menyebut bahwa ia sendiri yang
        memblokirnya bulan lalu. Itu persis kelas kegagalan yang kita habiskan
        waktu untuk hapus dari aplikasi ini.

        Kolom blocked dan pemeriksaannya di UnitStore.Validate SENGAJA
        dipertahankan. Keduanya tidak dapat dinyalakan dari mana pun kecuali
        menyunting basis data langsung — jalan darurat yang sadar, bukan tombol
        yang mudah tertekan. Menghapusnya menuntut migrasi skema lagi tanpa
        manfaat yang sepadan.
    */

    /// <summary>
    /// Menandai unit yang membuka siaran langsung dalam layar penuh.
    ///
    /// Dinyalakan untuk PC yang tercolok proyektor, dan hanya itu. Menyalakannya
    /// untuk seluruh kelas berarti empat puluh layar direbut siaran tanpa
    /// diminta — dan mahasiswa yang sedang mengerjakan sesuatu kehilangan
    /// layarnya di tengah pekerjaan.
    /// </summary>
    public Task<bool> SetAutoFullscreen(string unitId, bool enabled)
    {
        if (Authorize(PanelRights.ManageUnits, out _) is not { } panel)
            return Task.FromResult(false);

        var ok = units.SetAutoFullscreen(unitId, enabled);
        if (ok) activity.Write(panel.DisplayName,
            enabled ? "unit.fullscreen.on" : "unit.fullscreen.off", unitId);

        return Task.FromResult(ok);
    }

    public Task<bool> SetAlias(string unitId, string? alias)
    {
        if (Authorize(PanelRights.ManageUnits, out _) is not { } panel) return Task.FromResult(false);

        units.SetAlias(unitId, alias);
        activity.Write(panel.DisplayName, "unit.alias", unitId, alias);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Mengganti nama unit — di basis data dan di registri yang berjalan.
    ///
    /// Tabel penomoran di console.json hanya berlaku pada pendaftaran pertama,
    /// jadi mengubahnya saja tidak menyentuh unit yang sudah terdaftar. Tanpa
    /// jalur ini, asisten yang membetulkan sebuah nama akan melihatnya
    /// tersimpan dengan benar dan tidak berpengaruh apa pun — sampai PC itu
    /// didaftarkan ulang dari nol.
    /// </summary>
    public Task<string?> RenameUnit(string unitId, string newUnitId)
    {
        if (Authorize(PanelRights.ManageUnits, out _) is not { } panel)
            return Task.FromResult<string?>("Tidak berwenang mengubah unit");

        var (ok, reason) = units.Rename(unitId, newUnitId);
        if (!ok) return Task.FromResult(reason);

        // Registri disesuaikan HANYA setelah basis data berhasil. Urutan
        // sebaliknya membuat grid menampilkan nama yang gagal disimpan.
        registry.Rename(unitId, newUnitId.Trim());

        activity.Write(panel.DisplayName, "unit.rename", newUnitId.Trim(),
            $"sebelumnya {unitId}");
        log.LogInformation("Unit {Old} diganti nama menjadi {New}", unitId, newUnitId.Trim());

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Mendorong setelan akses yang baru ke seluruh agent.
    ///
    /// Dipanggil setelah layar Konfigurasi menyimpan. Tanpa ini, mengubah
    /// saklar hanya berlaku bagi unit yang mendaftar SETELAHNYA — asisten yang
    /// mematikan kewajiban login lalu melihat mahasiswa tetap dimintai NIM
    /// akan menyimpulkan saklarnya rusak, padahal ia hanya belum sampai.
    /// </summary>
    public async Task<bool> PublishAccessPolicy()
    {
        if (Authorize(PanelRights.Configure, out _) is not { } panel) return false;

        var policy = AgentHub.CurrentPolicy(config);
        await agents.Clients.All.SendAsync("Policy", policy);

        activity.Write(panel.DisplayName, "settings.policy",
            detail: $"login={policy.RequireStudentLogin} tamu={policy.AllowGuestAccess} "
                  + $"tolakKendali={policy.StudentsMayRejectControl}");

        log.LogInformation("Setelan akses didorong ke seluruh agent");
        return true;
    }

    /* ================= sesi ujian ================= */

    /// <summary>
    /// Memulai sesi ujian untuk SELURUH unit yang tersambung.
    ///
    /// Seluruh lab, bukan pilihan unit. Ujian berlaku untuk kelas, dan mode ujian
    /// yang dinyalakan per unit akan selalu melewatkan seseorang — mahasiswa yang
    /// terlambat, atau PC yang baru dinyalakan setelah sesi dimulai. Yang
    /// terlewat itu tidak akan terlihat sebagai kesalahan, hanya sebagai unit
    /// yang lini masanya kosong, dan kosong terbaca seperti bersih.
    /// </summary>
    public async Task<ExamSession?> StartExam(string title)
    {
        if (Authorize(PanelRights.ExamMode, out _) is not { } panel) return null;

        var session = exams.Start(title, panel.DisplayName);

        // Null berarti sudah ada sesi berjalan. Sesi yang sedang berjalan
        // dikembalikan supaya panel dapat menampilkan sesi mana — pesan
        // "gagal" tanpa menyebutkan sesinya tidak dapat ditindaklanjuti.
        if (session is null) return exams.Active();

        activity.Write(panel.DisplayName, "exam.started", detail: session.Title);
        log.LogInformation("Sesi ujian dimulai: {Title} oleh {Panel}", session.Title, panel.DisplayName);

        await BroadcastExamState(session);
        return session;
    }

    public async Task<bool> EndExam()
    {
        if (Authorize(PanelRights.ExamMode, out _) is not { } panel) return false;

        var ended = exams.End();

        /*
            Perintah berhenti DIKIRIM WALAU tidak ada sesi di basis data.

            Terlihat berlebihan, dan justru sebaliknya. Keadaan yang paling
            berbahaya di fitur ini adalah klien yang masih memantau sementara
            koordinator mengira sesinya sudah selesai — misalnya setelah
            koordinator dimulai ulang di tengah sesi. Mengirim perintah berhenti
            tanpa syarat berarti tombol "Akhiri" selalu benar-benar mematikan
            pemantauan, apa pun yang tercatat di basis data.
        */
        await agents.Clients.All.SendAsync("Execute", new EndExamCommand
        {
            IssuedBy = panel.DisplayName,
        });

        if (ended)
        {
            activity.Write(panel.DisplayName, "exam.ended");
            log.LogInformation("Sesi ujian diakhiri oleh {Panel}", panel.DisplayName);
        }

        await Clients.All.SendAsync("ExamState", (ExamSession?)null);
        return ended;
    }

    public Task<ExamSession?> GetExam() =>
        Task.FromResult(Authorize(PanelRights.ExamMode, out _) is null ? null : exams.Active());

    public Task<Dictionary<string, int>> GetExamCounts(long sessionId) =>
        Task.FromResult(Authorize(PanelRights.ExamMode, out _) is null
            ? []
            : exams.Counts(sessionId));

    /// <summary>
    /// Hitungan beserta peristiwa terakhir tiap unit, untuk daftar yang dapat dipindai.
    /// </summary>
    public Task<Dictionary<string, ExamUnitSummary>> GetExamSummaries(long sessionId) =>
        Task.FromResult(Authorize(PanelRights.ExamMode, out _) is null
            ? []
            : exams.Summaries(sessionId));

    public Task<ExamEvent[]> GetExamTimeline(long sessionId, string unitId) =>
        Task.FromResult(Authorize(PanelRights.ExamMode, out _) is null
            ? []
            : exams.Timeline(sessionId, unitId));

    /// <summary>
    /// Menyuruh seluruh unit mulai memantau, lalu mengabarkan panel lain.
    ///
    /// Dipisah menjadi metodenya sendiri karena unit yang mendaftar SETELAH sesi
    /// dimulai juga perlu disuruh — lihat AgentHub.Register. Dua tempat yang
    /// menyusun perintahnya sendiri-sendiri akan berbeda begitu satu medan
    /// ditambahkan.
    /// </summary>
    private async Task BroadcastExamState(ExamSession session)
    {
        await agents.Clients.All.SendAsync("Execute", new BeginExamCommand
        {
            SessionId = session.Id,
            Title = session.Title,
            IssuedBy = session.StartedBy,
        });

        await Clients.All.SendAsync("ExamState", session);
    }


    /// <summary>
    /// Mengirim ke agent, dan MENCATAT bila pengirimannya gagal.
    ///
    /// "_ = ...SendAsync(...)" membuang Task-nya beserta setiap pengecualian di
    /// dalamnya. Koneksi yang sudah mati, agent yang baru saja terputus, atau
    /// pesan yang tidak dapat diserialisasi — ketiganya menghilang tanpa jejak,
    /// dan panel tetap melaporkan perintahnya terkirim.
    ///
    /// Itu kelas kegagalan yang paling mahal di seluruh aplikasi ini: asisten
    /// menekan tombol, layarnya bilang berhasil, dan tidak ada apa pun yang
    /// terjadi di PC mahasiswa. Tidak ada satu tempat pun untuk memulai
    /// menelusurinya.
    ///
    /// Task-nya tetap tidak ditunggu — hub tidak boleh tertahan oleh satu agent
    /// yang lambat — tetapi kegagalannya sekarang masuk log.
    /// </summary>
    private void FireToAgents(Task sending, string what)
    {
        _ = sending.ContinueWith(t =>
                log.LogWarning("Pengiriman {What} ke agent gagal: {Message}",
                    what, t.Exception?.InnerException?.Message ?? t.Exception?.Message),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public Task<Dictionary<string, string>> GetMacAddresses()
    {
        if (Authorize(PanelRights.PowerControl, out _) is null)
            return Task.FromResult(new Dictionary<string, string>());

        return Task.FromResult(units.MacAddresses());
    }

    /* ================= panel lain ================= */

    public Task<PanelSummary[]> GetPanels()
    {
        if (Authorize(PanelRights.ManageCredentials, out _) is null)
            return Task.FromResult(Array.Empty<PanelSummary>());

        return Task.FromResult(panels.Snapshot().Select(p => new PanelSummary(
            p.DeviceId, p.DisplayName, p.Role, p.RequestedRole, p.Revoked,
            p.Online, p.WiredConnection, p.LastSeen)).ToArray());
    }

    /// <summary>
    /// Membuka jendela pendaftaran dan mengembalikan kodenya.
    ///
    /// Hanya pemegang ManageCredentials — yaitu Superadmin. Kalau Asisten biasa
    /// dapat membukanya, maka Asisten dapat mendaftarkan panel baru dan
    /// menyetujuinya sebagai Asisten pula, dan pembatasan peran berhenti
    /// membatasi apa pun setelah satu langkah.
    /// </summary>
    public Task<EnrollmentWindowDto> OpenEnrollment()
    {
        var panel = Authorize(PanelRights.ManageCredentials, out _);
        if (panel is null) return Task.FromResult(EnrollmentWindowDto.Closed);

        var until = enrollment.Open();
        activity.Write(panel.DisplayName, "panel.enrollment.opened",
            detail: $"berlaku {PanelEnrollment.Lifetime.TotalMinutes:0} menit");

        return Task.FromResult(new EnrollmentWindowDto(true, until, identity.Thumbprint));
    }

    public Task<EnrollmentWindowDto> CloseEnrollment()
    {
        var panel = Authorize(PanelRights.ManageCredentials, out _);
        if (panel is null) return Task.FromResult(EnrollmentWindowDto.Closed);

        enrollment.Close();
        activity.Write(panel.DisplayName, "panel.enrollment.closed");
        return Task.FromResult(EnrollmentWindowDto.Closed);
    }

    public Task<EnrollmentWindowDto> GetEnrollment()
    {
        if (Authorize(PanelRights.ManageCredentials, out _) is null)
            return Task.FromResult(EnrollmentWindowDto.Closed);

        return Task.FromResult(enrollment.IsOpen
            ? new EnrollmentWindowDto(true, enrollment.ExpiresAt, identity.Thumbprint)
            : EnrollmentWindowDto.Closed);
    }

    public async Task<bool> ApprovePanel(string deviceId, PanelRole role)
    {
        if (Authorize(PanelRights.ManageCredentials, out _) is not { } panel) return false;

        var ok = panels.Approve(deviceId, role);
        if (!ok) return false;

        activity.Write(panel.DisplayName, "panel.approved", detail: $"{deviceId} sebagai {role}");
        await NotifyRoleChanged(deviceId);
        return true;
    }

    public async Task<bool> RevokePanel(string deviceId)
    {
        if (Authorize(PanelRights.ManageCredentials, out _) is not { } panel) return false;

        var ok = panels.Revoke(deviceId);
        if (!ok) return false;

        activity.Write(panel.DisplayName, "panel.revoked", detail: deviceId);
        await NotifyRoleChanged(deviceId);
        return true;
    }

    /// <summary>
    /// Memberi tahu panel yang bersangkutan bahwa perannya berubah.
    ///
    /// TANPA INI PERSETUJUAN TIDAK TERLIHAT OLEH YANG DISETUJUI.
    ///
    /// Panel hanya mengetahui perannya dari jawaban RegisterPanel, dan itu
    /// dijalankan sekali saat menyambung. Persetujuan yang terjadi sesudahnya
    /// mengubah keadaan di sisi Superadmin saja: dosen tetap melihat "menunggu
    /// persetujuan" selamanya, sementara asisten melihat panel itu sudah
    /// disetujui. Dua layar, dua kebenaran, dan tidak ada galat di antaranya.
    ///
    /// Satu-satunya jalan keluar sebelum ini adalah menutup lalu membuka
    /// aplikasinya — yang akan ditemukan orang, lalu dikira memang begitu
    /// caranya.
    /// </summary>
    private async Task NotifyRoleChanged(string deviceId)
    {
        var target = panels.Snapshot().FirstOrDefault(p => p.DeviceId == deviceId);
        if (target?.ConnectionId is not { } connectionId) return;

        await Clients.Client(connectionId).SendAsync("Role", new PanelWelcome
        {
            Accepted = true,
            Role = target.Role,
            Rights = PanelRegistry.RightsFor(target.Role),
            LabName = config.LabName,
        });
    }
}

/// <summary>Panel sebagaimana dilihat Superadmin di layar persetujuan.</summary>
public sealed record PanelSummary(
    string DeviceId, string DisplayName, PanelRole Role, PanelRole RequestedRole,
    bool Revoked, bool Online, bool Wired, DateTimeOffset LastSeen);

/// <summary>
/// Keadaan jendela pendaftaran, untuk ditampilkan di layar Superadmin.
///
/// Sidik jari ikut dikirim supaya asisten dapat membandingkannya dengan yang
/// ditampilkan panel dosen sebelum menyetujui. Sejak kode pendaftaran
/// dihapuskan, perbandingan mata inilah satu-satunya pengikat antara "lab yang
/// dimaksud asisten" dan "lab yang benar-benar dihubungi panel".
/// </summary>
public sealed record EnrollmentWindowDto(
    bool Open, DateTimeOffset? ExpiresAt, string? Thumbprint)
{
    public static readonly EnrollmentWindowDto Closed = new(false, null, null);
}

/// <summary>
/// Hasil pengiriman chat.
///
/// Membawa alasan kegagalan, bukan sekadar bool. "Pesan tidak terkirim" tanpa
/// sebab memaksa orang menebak antara unit yang mati, wewenang yang kurang,
/// dan pesan yang kosong — tiga hal dengan tiga tindakan lanjutan berbeda.
/// </summary>
public sealed record ChatSendResult(bool Sent, int Reached, string? RejectReason);
