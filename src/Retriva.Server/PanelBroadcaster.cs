using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Retriva.Server.Data;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Mendorong perubahan keadaan ke seluruh panel yang sudah disetujui.
///
/// Sebelumnya panel membaca langsung dari UnitRegistry dan menyimak event
/// statis AgentHub — mungkin karena panel dan koordinator berada dalam satu
/// proses. Begitu panel dosen berjalan di laptop terpisah, jalur itu hilang
/// sama sekali, dan panelnya akan tampak hidup namun tidak pernah berubah.
///
/// Di sini seluruh dorongan melewati satu tempat, sehingga panel di PC Asisten
/// dan panel di laptop dosen menerima hal yang sama lewat jalan yang sama.
///
/// PENGGABUNGAN ADALAH INTINYA, BUKAN OPTIMISASI.
///
/// Empat puluh agent mengirim pratinjau tiap dua detik, dan setiap kedatangan
/// mengubah keadaan. Mengirim satu daftar unit lengkap per kejadian berarti
/// dua puluh kiriman per detik ke setiap panel, masing-masing berisi empat
/// puluh gambar base64. Panel dosen di Wi-Fi akan tersedak oleh datanya
/// sendiri. Perubahan dikumpulkan lalu dikirim pada laju tetap.
/// </summary>
public sealed class PanelBroadcaster(
    IHubContext<PanelHub> panelHub,
    PanelRegistry panels,
    UnitRegistry registry,
    UnitStore units,
    ChatStore chats,
    ActivityLog activity,
    BatchTracker batches,
    BroadcastRelay relay,
    ILogger<PanelBroadcaster> log) : BackgroundService
{
    /// <summary>
    /// Laju dorongan daftar unit.
    ///
    /// Dua kali per detik: cukup cepat sehingga menekan tombol terasa langsung
    /// berpengaruh, cukup lambat sehingga empat puluh pratinjau tidak dikirim
    /// ulang lebih cepat daripada kedatangannya.
    /// </summary>
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(500);

    private volatile bool _unitsDirty = true;
    private volatile bool _activityDirty = true;
    private volatile bool _panelsDirty = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.Changed += MarkUnitsDirty;

        /*
            Perubahan daftar panel didorong sendiri, tidak menunggu layar dibuka.

            Sebelumnya daftar ini hanya diambil saat layar Panel admin dibuka.
            Akibatnya permintaan bergabung yang datang selagi layar itu terbuka
            tidak muncul sama sekali — asisten harus berpindah menu lalu kembali,
            dan sementara itu dosen menunggu di depan kelas tanpa tahu
            permintaannya sudah sampai atau belum.
        */
        panels.Changed += MarkPanelsDirty;
        relay.Changed += OnBroadcastChanged;
        activity.Written += MarkActivityDirty;
        batches.Completed += OnBatchCompleted;
        AgentHub.ThumbnailArrived += OnThumbnail;
        AgentHub.ChatArrived += OnChat;
        AgentHub.HelpChanged += OnHelp;
        AgentHub.FileProgressArrived += OnFileProgress;

        try
        {
            using var timer = new PeriodicTimer(PushInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_unitsDirty && !_activityDirty && !_panelsDirty) continue;

                var targets = panels.ApprovedConnections();
                if (targets.Count == 0) continue;

                var pushUnits = _unitsDirty;
                var pushActivity = _activityDirty;
                var pushPanels = _panelsDirty;
                _unitsDirty = false;
                _activityDirty = false;
                _panelsDirty = false;

                try
                {
                    var clients = panelHub.Clients.Clients(targets);

                    if (pushUnits)
                        await clients.SendAsync("Units", BuildUnits(), stoppingToken);

                    if (pushActivity)
                        await clients.SendAsync("Activity", BuildActivity(), stoppingToken);

                    // Daftar panel hanya berarti bagi pemegang ManageCredentials,
                    // tetapi dikirim ke semua: menyaring per penerima menuntut
                    // pengiriman satu per satu, dan isinya bukan rahasia — nama
                    // dan peran panel yang sudah saling terlihat di lab.
                    if (pushPanels)
                        await clients.SendAsync("Panels", BuildPanels(), stoppingToken);

                    // Jumlah penerima ikut didorong selagi siaran berjalan.
                    //
                    // Angkanya bertambah seiring unit melaporkan frame pertamanya
                    // lewat denyut, jadi ia tidak dapat hanya dikirim sekali saat
                    // klaim: yang dilihat dosen akan berhenti di nol selamanya,
                    // padahal seluruh kelas sudah menerima.
                    if (pushUnits && relay.Holder is not null)
                        await clients.SendAsync("Broadcast", BuildBroadcastState(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Satu panel yang putus tepat saat pengiriman tidak boleh
                    // menghentikan pendorong untuk semua panel lain. Ditandai
                    // kotor lagi supaya percobaan berikutnya tetap terjadi.
                    _unitsDirty |= pushUnits;
                    _activityDirty |= pushActivity;
                    _panelsDirty |= pushPanels;
                    log.LogWarning(ex, "Gagal mendorong keadaan ke panel");
                }
            }
        }
        finally
        {
            // Event statis AgentHub hidup selama proses hidup. Tanpa pelepasan
            // ini, layanan yang dihentikan tetap dipanggil oleh setiap pratinjau
            // yang datang — memegang objeknya tetap hidup dan mendorong ke hub
            // yang sudah tidak dipakai.
            registry.Changed -= MarkUnitsDirty;
            panels.Changed -= MarkPanelsDirty;
            relay.Changed -= OnBroadcastChanged;
            activity.Written -= MarkActivityDirty;
            batches.Completed -= OnBatchCompleted;
            AgentHub.ThumbnailArrived -= OnThumbnail;
            AgentHub.ChatArrived -= OnChat;
            AgentHub.HelpChanged -= OnHelp;
            AgentHub.FileProgressArrived -= OnFileProgress;
        }
    }

    private void MarkUnitsDirty() => _unitsDirty = true;
    private void MarkPanelsDirty() => _panelsDirty = true;

    /// <summary>
    /// Keadaan siaran didorong ke SELURUH panel, bukan hanya yang menyiarkan.
    ///
    /// Panel lain perlu tahu siaran sedang berjalan dan milik siapa — tanpa itu
    /// dua orang menekan "Mulai siaran" bersamaan, yang kedua merebut tanpa
    /// sadar, dan yang pertama kehilangan kelasnya di tengah penjelasan.
    /// </summary>
    private void OnBroadcastChanged()
    {
        var targets = panels.ApprovedConnections();
        if (targets.Count == 0) return;

        _ = panelHub.Clients.Clients(targets).SendAsync("Broadcast", BuildBroadcastState());
    }

    /// <summary>
    /// Keadaan siaran, dengan jumlah penerima YANG SUNGGUHAN.
    ///
    /// Versi sebelumnya menghitungnya sebagai "ada frame diteruskan atau tidak"
    /// lalu mengirimkannya sebagai jumlah penerima. Dosen yang menyiarkan ke
    /// empat puluh PC akan melihat angka 1 dan menyimpulkan tiga puluh sembilan
    /// rusak — angka yang terlihat berwenang dan tidak berarti apa pun.
    ///
    /// Sekarang dihitung dari unit yang MELAPORKAN telah merakit frame utuh.
    /// Multicast UDP tidak punya jalur balik, jadi ini satu-satunya sumber yang
    /// dapat mengetahuinya: unit itu sendiri.
    /// </summary>
    private BroadcastState BuildBroadcastState()
    {
        var holder = relay.Holder;
        var (_, mbps) = relay.Stats;

        var receiving = holder is null
            ? 0
            : registry.Snapshot().Count(u => u.Status != UnitStatus.Offline && u.BroadcastFrames > 0);

        return new BroadcastState
        {
            Active = holder is not null,
            SourceLabel = holder?.SourceLabel,
            HeldByPanel = holder?.DisplayName,
            Receivers = receiving,
            Mbps = mbps,
        };
    }

    private PanelSummary[] BuildPanels() => [.. panels.Snapshot().Select(p => new PanelSummary(
        p.DeviceId, p.DisplayName, p.Role, p.RequestedRole, p.Revoked,
        p.Online, p.WiredConnection, p.LastSeen))];
    private void MarkActivityDirty() => _activityDirty = true;
    /// <summary>
    /// Permintaan bantuan menandai daftar kotor DAN diteruskan segera.
    ///
    /// Keduanya, bukan salah satu: daftar perlu diperbarui supaya penanda
    /// menyala di kartu unitnya, dan peristiwanya perlu sampai utuh supaya
    /// panel dapat memunculkan pemberitahuan. Kalau hanya daftar yang dikirim,
    /// panel harus menebak mana yang baru dengan membandingkan dua daftar —
    /// dan tebakan itu meleset setiap kali dua orang mengangkat tangan dalam
    /// setengah detik yang sama.
    /// </summary>
    private void OnHelp(HelpRequest request)
    {
        _unitsDirty = true;

        var targets = panels.ApprovedConnections();
        if (targets.Count == 0) return;
        _ = panelHub.Clients.Clients(targets).SendAsync("Help", request);
    }

    /// <summary>
    /// Pratinjau diteruskan satu per satu, tidak menandai daftar unit kotor.
    ///
    /// Menandainya kotor akan memaksa seluruh daftar dikirim ulang setiap satu
    /// pratinjau tiba — dua puluh kali per detik dengan empat puluh unit. Yang
    /// berubah hanya satu gambar, jadi yang dikirim juga hanya satu gambar.
    /// </summary>
    private void OnThumbnail(string unitId, string dataUrl)
    {
        var targets = panels.ApprovedConnections();
        if (targets.Count == 0) return;
        _ = panelHub.Clients.Clients(targets).SendAsync("Thumbnail", unitId, dataUrl);
    }

    /// <summary>
    /// Hasil perintah massal, diteruskan segera.
    ///
    /// Ini justru jawaban atas tombol yang baru ditekan orang. Menahannya
    /// setengah detik membuat "40 unit dikunci" muncul terlambat, dan yang
    /// menekan sudah lebih dulu menyimpulkan perintahnya tidak jalan.
    /// </summary>
    private void OnBatchCompleted(BatchSummary summary)
    {
        // Hasilnya dicatat di sini, bukan di panel.
        //
        // Sebelumnya panel yang menuliskannya — yang berarti hasil perintah
        // hanya tercatat kalau ada panel yang kebetulan sedang terbuka, dan
        // dua panel yang terbuka bersamaan menuliskannya dua kali. Batasnya
        // memang di sini: yang tahu batch selesai adalah pelacaknya.
        activity.Write("system", summary.Action + ".hasil", detail: summary.Describe());

        var targets = panels.ApprovedConnections();
        if (targets.Count == 0) return;
        _ = panelHub.Clients.Clients(targets).SendAsync("Batch", summary);
    }

    private void OnFileProgress(FileTransferProgress progress)
    {
        var targets = panels.ApprovedConnections();
        if (targets.Count == 0) return;
        _ = panelHub.Clients.Clients(targets).SendAsync("FileProgress", progress);
    }

    private ActivityEntryDto[] BuildActivity() => activity.Recent(40)
        .Select(e => new ActivityEntryDto(e.UnitId, e.Actor, e.Action, e.Detail, e.CreatedAt))
        .ToArray();

    /// <summary>
    /// Chat diteruskan segera, tidak ikut digabung.
    ///
    /// Pesan tunggal itu kecil, dan penundaan setengah detik pada percakapan
    /// terasa sebagai aplikasi yang tersendat — berbeda dengan pratinjau yang
    /// memang tidak ada yang menunggunya.
    /// </summary>
    private void OnChat(ChatMessage message)
    {
        var targets = panels.ApprovedConnections();
        if (targets.Count == 0) return;

        _unitsDirty = true; // penghitung belum dibaca ikut berubah
        _ = panelHub.Clients.Clients(targets).SendAsync("Chat", message);
    }

    private UnitSnapshot[] BuildUnits() =>
        UnitSnapshots.Build(registry, units, chats, includeThumbnails: false);
}
