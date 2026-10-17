using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Retriva.Media;
using Retriva.Shared.Broadcast;
using Retriva.Shared;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;
using Retriva.Shared.Security;
using Retriva.Server.Data;
using Retriva.Server.Files;
using Retriva.Server;
using Retriva.Ui;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using Retriva.Shared.Update;

namespace Retriva.Admin;

/// <summary>
/// Jendela utama: hanya meng-host WebView2. Seluruh antarmuka digambar React.
/// C# berperan sebagai backend lokal, bukan sebagai penggambar UI.
/// </summary>
public sealed class MainWindow : ChromeWindow
{
    private const string VirtualHost = "retriva.local";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Satu-satunya jalan panel ini menyentuh keadaan lab.
    ///
    /// Sebelumnya jendela ini memegang UnitRegistry, IHubContext, ActivityLog,
    /// BatchTracker, ChatStore, dan UnitStore secara langsung — dan itu jalan
    /// karena panel dan koordinator kebetulan berada dalam satu proses. Begitu
    /// panel dosen berjalan di laptop terpisah, tidak satu pun dari objek itu
    /// ada di sana, dan panelnya akan terbuka lalu diam selamanya tanpa satu
    /// pesan galat pun: daftar unit kosong terlihat sama persis dengan lab yang
    /// memang sedang libur.
    /// </summary>
    private readonly SuperadminLink _link;
    private IntPtr _hwnd;
    private readonly HashSet<string> _pendingHelpUnits = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _helpBadge;
    private string? _pendingUpdateInstallerPath;

    /// <summary>
    /// Penangkap dan pengirim siaran. Berjalan di konsol sendiri, bukan lewat
    /// agent — konsol adalah aplikasi sesi pengguna, jadi tidak terkena
    /// isolasi Session 0 dan dapat menangkap layarnya langsung.
    /// </summary>
    private BroadcastSession? _broadcast;

    /// <summary>
    /// Mendorong pratinjau siaran ke UI sekali per detik.
    ///
    /// Sengaja bukan tiap frame: 1080p menghasilkan sekitar 250 KB per frame,
    /// dan mengirimkannya sebagai base64 pada 15 fps berarti 5 MB per detik
    /// menyeberangi jembatan WebView2 — biaya besar untuk kotak pratinjau kecil.
    /// Satu frame per detik sudah cukup membuktikan siaran benar-benar berjalan.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private readonly FileVault _vault;
    private readonly RemoteView _remote = new();

    /// <summary>
    /// Denyut untuk kendali jarak jauh, jauh lebih cepat daripada pratinjau
    /// siaran. Di sini gambarnya adalah alat kerja: kursor yang tertinggal
    /// setengah detik membuat mengarahkan apa pun jadi tidak mungkin. Biaya
    /// base64 lewat jembatan WebView2 diterima sebagai konsekuensinya, karena
    /// hanya satu unit yang ditampilkan pada satu waktu.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _remoteTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100),
    };

    private readonly ILogger _log;
    private readonly ConsoleConfig _config;
    private readonly string _thumbprint;
    private readonly WebView2 _web = new();
    private bool _pageLoaded;

    /*
        Judul jendela menyebut peran YANG SEBENARNYA.

        Sebelumnya ia dibaca dari ConsoleConfig.Role — nilai yang menggambarkan
        peran koordinator dalam pasangan primer/sekunder, dan sama sekali tidak
        berhubungan dengan apakah mesin ini menjalankan lab. Akibatnya panel
        Dosen berjudul "Panel Asisten" hanya karena console.json-nya baru
        dibuat dengan nilai bawaan.

        Ini menyesatkan tepat pada saat orang paling membutuhkannya: ketika ada
        dua jendela terbuka dan pertanyaannya adalah yang mana yang mana.
    */
    public MainWindow(SuperadminLink link, FileVault vault, ConsoleConfig config,
                      string thumbprint, ILogger log)
        : base("Retriva", link.IsSuperadminMachine ? "Superadmin" : "Panel admin", "admin")
    {
        _log = log;
        _vault = vault;
        _link = link;
        _config = config;
        _thumbprint = thumbprint;

        Width = 1180;
        Height = 760;
        MinWidth = 900;
        MinHeight = 600;
        SetBody(_web);
        AssetDiagnostic += m => _log.LogWarning("{Message}", m);

        Loaded += async (_, _) => await InitializeWebViewAsync();

        // Seluruh keadaan kini datang lewat satu jalur, bukan dari enam objek
        // yang kebetulan ada di proses yang sama.
        _link.UnitsChanged += OnUnitsChanged;
        _link.ActivityChanged += OnActivityChanged;
        _link.ThumbnailArrived += OnThumbnailArrived;
        _link.ChatArrived += OnChatArrived;
        _link.FileProgress += OnFileProgress;
        _link.BatchCompleted += OnBatchCompleted;
        _link.StatusChanged += OnLinkStatusChanged;

        // Sesi ujian yang dimulai panel LAIN ikut tampil di sini. Tanpa ini,
        // asisten akan melihat tombol "Mulai sesi" untuk sesi yang sudah
        // berjalan, dan dapat menekan "Akhiri" tanpa tahu sesi siapa.
        _link.ExamChanged += () => Run(PushExamAsync, "menyegarkan keadaan sesi ujian");
        _link.PanelsChanged += OnPanelsChanged;
        _link.BroadcastChanged += OnBroadcastStateChanged;
        _link.HelpChanged += OnHelpChanged;

        Activated += (_, _) => StopFlash();

        _previewTimer.Tick += (_, _) => PushBroadcastPreview();
        _sourcePreviewTimer.Tick += (_, _) => PushSourcePreview();
        _remoteTimer.Tick += (_, _) => PushRemoteFrame();
        Closed += (_, _) =>
        {
            _link.UnitsChanged -= OnUnitsChanged;
            _link.ActivityChanged -= OnActivityChanged;
            _link.ThumbnailArrived -= OnThumbnailArrived;
            _link.ChatArrived -= OnChatArrived;
            _link.FileProgress -= OnFileProgress;
            _link.BatchCompleted -= OnBatchCompleted;
            _link.StatusChanged -= OnLinkStatusChanged;
            _link.PanelsChanged -= OnPanelsChanged;
            _link.BroadcastChanged -= OnBroadcastStateChanged;
            _link.HelpChanged -= OnHelpChanged;
            _previewTimer.Stop();
            StopSourcePreview();
            _remoteTimer.Stop();
            _remote.Dispose();
            _ = _broadcast?.DisposeAsync();

            if (!string.IsNullOrWhiteSpace(_pendingUpdateInstallerPath) && File.Exists(_pendingUpdateInstallerPath))
            {
                _log.LogInformation("Aplikasi ditutup dengan pembaruan tertunda. Menjalankan silent update ke {Path}...", _pendingUpdateInstallerPath);
                UpdateService.ApplySilentInstall(_pendingUpdateInstallerPath, "panel");
            }
        };
    }

    /// <summary>
    /// Jendela admin disembunyikan dari tangkapan layar, seperti OBS
    /// menyembunyikan jendelanya sendiri. Tanpa ini, dosen yang menyiarkan
    /// seluruh layar ikut menyiarkan daftar unit, kode pendaftaran, dan
    /// pratinjau siarannya sendiri — yang menghasilkan efek cermin.
    /// </summary>
    protected override void OnWindowHandleReady(IntPtr handle)
    {
        _hwnd = handle;
        _log.LogInformation("Pengecualian capture: {Result}", CaptureExclusion.Apply(handle));
    }

    private async Task InitializeWebViewAsync()
    {
        /*
            Lingkungan WebView2 dibuat dengan folder data yang DITENTUKAN.

            EnsureCoreWebView2Async() tanpa argumen membuat WebView2 menaruh
            cache-nya di sebelah exe. Di C:\Program Files itu ditolak, dan
            galatnya hanya berbunyi "Access is denied (E_ACCESSDENIED)" — tanpa
            menyebut WebView2 maupun folder mana yang bermasalah. Lihat catatan
            panjang di Retriva.Ui/WebViewData.cs.
        */
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: WebViewData.FolderFor("admin"));

        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;

        core.WebMessageReceived += OnWebMessageReceived;

        // Jaring pengaman: dorong data begitu halaman selesai dimuat, tanpa
        // menunggu pesan "ready" dari React. Kalau jembatannya bermasalah,
        // UI tetap terisi dan penyebabnya lebih mudah dilacak.
        core.NavigationCompleted += (_, _) =>
        {
            _pageLoaded = true;
            PushSettings();
            PushLinkStatus();
            TrySeed();
            _ = Task.Run(() => CheckForAppUpdatesAsync(manual: false));
        };

        // Saat menggarap UI, arahkan ke dev server Vite supaya hot reload jalan:
        //   $env:RETRIVA_UI_URL = "http://localhost:5173"
        var devUrl = Environment.GetEnvironmentVariable("RETRIVA_UI_URL");
        if (!string.IsNullOrWhiteSpace(devUrl))
        {
            core.Navigate(devUrl);
            return;
        }

        // Produksi: sajikan wwwroot lewat host virtual, bukan file:// —
        // supaya ES module dan fetch tidak terhalang aturan origin.
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(wwwroot, "index.html")))
        {
            // Jalur yang dicari ikut ditampilkan.
            //
            // Versi sebelumnya hanya menyuruh menjalankan npm run build, dan itu
            // menyesatkan justru pada kasus yang paling mungkin terjadi: build
            // React BERHASIL tetapi keluarannya mendarat di folder lain daripada
            // yang dicari csproj. Pesan yang menyuruh mengulangi langkah yang
            // sudah benar membuat orang mencari di tempat yang salah.
            core.NavigateToString(
                "<body style='font-family:Segoe UI;padding:40px'>"
              + "<h2>UI belum ada di folder output</h2>"
              + "<p>Dicari di:<br><code>" + Path.Combine(wwwroot, "index.html") + "</code></p>"
              + "<p>Bila <code>npm run build</code> sudah dijalankan, periksa apakah "
              + "<code>outDir</code> di <code>ui/vite.config.js</code> menunjuk folder yang "
              + "sama dengan <code>Content Include</code> di csproj.</p></body>");
            return;
        }

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate($"https://{VirtualHost}/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = ExtractJson(e);
        if (json is null) return;

        UiMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<UiMessage>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[bridge] pesan tidak dikenali: {ex.Message} :: {json}");
            return;
        }
        if (msg is null) return;

        switch (msg.Type)
        {
            case "ready":
                PushSettings();
                PushLinkStatus();
                PushUnits();
                PushActivity();
                PushCaptureSources();
                break;

            case "checkUpdates":
                _ = Task.Run(() => CheckForAppUpdatesAsync(manual: true));
                break;

            case "installUpdateNow":
                if (!string.IsNullOrWhiteSpace(_pendingUpdateInstallerPath) && File.Exists(_pendingUpdateInstallerPath))
                {
                    UpdateService.ApplySilentInstall(_pendingUpdateInstallerPath, "panel");
                    Close();
                }
                else
                {
                    PushToast("Pembaruan sedang diunduh di latar belakang. Silakan tunggu beberapa saat.", false);
                }
                break;

            case "refreshActivity":
                // Meminta ulang, bukan menggambar ulang salinan yang ada.
                // Menggambar ulang membuat tombol segarkan tidak pernah
                // mengubah apa pun — persis kebalikan dari yang dijanjikannya.
                Run(async () =>
                {
                    var entries = await _link.GetActivityAsync(40);
                    await Dispatcher.InvokeAsync(() => { _activity = entries; PushActivity(); });
                }, "menyegarkan jejak aktivitas");
                break;

            case "listSources":
                PushCaptureSources();
                break;

            case "ping":
                Dispatch(new PingCommand(), "command.ping");
                break;

            case "saveSettings":
                SaveSettings(json);
                break;

            case "powerOff":
            case "restart":
            case "abortShutdown":
            case "powerOffAll":
            case "restartAll":
            case "abortAll":
            case "powerOnAll":
                HandlePower(msg.Type, json);
                break;

            case "lock":
            case "unlock":
            case "lockAll":
            case "unlockAll":
                HandleLock(msg.Type, json);
                break;

            case "broadcastUnit":
            case "broadcastStart":
            case "broadcastStop":
            case "broadcastFreeze":
                HandleBroadcast(msg.Type, json);
                break;

            case "previewSource":
            case "previewStop":
                HandleSourcePreview(msg.Type, json);
                break;

            case "remoteBegin":
            case "remoteEnd":
                HandleRemote(msg.Type, json);
                break;

            case "remoteInput":
                HandleRemoteInput(json);
                break;

            case "filePick":
            case "fileSend":
            case "fileClear":
                HandleFiles(msg.Type, json);
                break;

            case "chatSend":
            case "chatOpen":
            case "helpAck":
            case "chatClear":
                HandleChat(msg.Type, json);
                break;

            case "panelsRefresh":
            case "enrollmentOpen":
            case "enrollmentClose":
            case "panelApprove":
            case "panelRevoke":
            case "unitAlias":
            case "unitRename":
            case "unitFullscreen":
            case "linkJoin":
            case "linkRetry":
            case "linkReset":
            case "linkBecomeSuperadmin":
            case "examStart":
            case "examEnd":
            case "examRefresh":
            case "examTimeline":
                HandlePanels(msg.Type, json);
                break;

            default:
                Debug.WriteLine($"[bridge] jenis pesan belum ditangani: {msg.Type}");
                break;
        }
    }

    /// <summary>
    /// React mengirim lewat postMessage(string). Untuk pesan bertipe string,
    /// WebMessageAsJson mengembalikan string ber-quote — bukan objek JSON.
    /// Metode ini menerima kedua bentuk supaya jembatan tidak rapuh.
    /// </summary>
    private static string? ExtractJson(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Diawali kutip berarti isinya string, bukan objek — buka satu lapis.
            return raw.TrimStart().StartsWith('"')
                ? JsonSerializer.Deserialize<string>(raw)
                : raw;
        }
        catch (JsonException)
        {
            try { return e.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return null; }
        }
    }

    /* ================= keadaan dari Superadmin ================= */

    /// <summary>
    /// Salinan terakhir yang diterima dari Superadmin.
    ///
    /// Disimpan karena penangan pesan dari UI berjalan sinkron dan sering
    /// perlu menjawab "unit mana yang online" seketika. Boleh tertinggal
    /// paling lama setengah detik; Superadmin tetap memeriksa ulang sebelum
    /// mengirim apa pun, jadi salinan yang basi tidak berakibat perintah
    /// mendarat di unit yang salah — paling jauh berakibat tombol yang tampak
    /// aktif padahal unitnya baru saja putus.
    /// </summary>
    private IReadOnlyList<UnitSnapshot> _units = [];
    private IReadOnlyList<ActivityEntryDto> _activity = [];

    /// <summary>
    /// Pratinjau per unit, dipisah dari daftar unit.
    ///
    /// Dorongan berkala dari Superadmin sengaja tidak membawa gambar — empat
    /// puluh base64 dua kali per detik akan menenggelamkan panel dosen di
    /// Wi-Fi. Gambarnya datang satu-satu dan disimpan di sini supaya tidak
    /// hilang setiap daftar unit diperbarui.
    /// </summary>
    private readonly Dictionary<string, string> _thumbnails = [];

    private bool _seeded;

    /// <summary>
    /// Memuat keadaan awal begitu KEDUA syaratnya terpenuhi.
    ///
    /// Halaman selesai dimuat dan sambungan menjadi siap adalah dua peristiwa
    /// yang urutannya tidak dijamin: WebView2 yang sudah panas menyelesaikan
    /// navigasinya lebih cepat daripada jabat tangan TLS. Memuat dari salah
    /// satu saja berarti setengah waktu panel terbuka kosong dengan pesan
    /// "sambungan belum aktif" — lalu diam, karena tidak ada yang mencoba lagi.
    ///
    /// Karena itu dipanggil dari keduanya, dan yang datang belakangan yang
    /// benar-benar menjalankannya.
    /// </summary>
    private void TrySeed()
    {
        if (_seeded || !_pageLoaded || !_link.Ready) return;
        _seeded = true;
        Run(SeedFromSuperadminAsync, "memuat keadaan awal");
    }

    private async Task SeedFromSuperadminAsync()
    {
        // Pemuatan pertama meminta langsung, tidak menunggu dorongan berkala.
        // Menunggu berarti panel terbuka dengan layar kosong selama setengah
        // detik pertama — dan layar kosong tidak dapat dibedakan dari lab yang
        // memang sedang tidak ada isinya.
        var units = await _link.GetUnitsAsync();
        var entries = await _link.GetActivityAsync(40);

        /*
            SELURUH SENTUHAN KE KEADAAN UI TERJADI DI THREAD UI.

            Metode ini dijalankan lewat Run, yang memakai Task.Run — jadi
            badannya berada di thread pool. WebView2 terikat pada thread yang
            membuatnya dan melempar "The calling thread cannot access this
            object because a different thread owns it" begitu disentuh dari
            tempat lain.

            Kamus pratinjau ikut dipindahkan ke sini, bukan hanya pendorongnya:
            ia dibaca thread UI setiap kali daftar unit digambar, dan menulisnya
            dari thread lain adalah balapan yang gejalanya jauh lebih jarang dan
            jauh lebih membingungkan daripada pengecualian yang barusan muncul.
        */
        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var u in units)
            {
                if (u.ThumbnailDataUrl is { } dataUrl) _thumbnails[u.UnitId] = dataUrl;
            }

            _units = units;
            _activity = entries;
            PushUnits();
            PushActivity();
        });
    }

    private void OnUnitsChanged(UnitSnapshot[] units) => Dispatcher.BeginInvoke(() =>
    {
        _units = units;
        PushUnits();
        SyncHelpTaskbar();
    });

    private void OnActivityChanged(ActivityEntryDto[] entries) => Dispatcher.BeginInvoke(() =>
    {
        _activity = entries;
        PushActivity();
    });

    private void OnLinkStatusChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Kegagalan sambungan dibersihkan begitu sambungan benar-benar jadi.
        // Dibiarkan, pesan "tidak dapat menyambung ke 192.168.1.9" akan
        // bertahan di layar panel yang sudah tersambung ke alamat yang benar.
        if (_link.Connected) _linkFailure = null;

        PushLinkStatus();

        // Sambungan yang putus membatalkan penyemaian, supaya pemulihannya
        // memuat ulang keadaan. Tanpa ini panel yang sempat terputus akan
        // tersambung kembali lalu menampilkan daftar unit dari sebelum putus,
        // tanpa satu pun tanda bahwa isinya sudah basi.
        if (!_link.Ready) _seeded = false;
        TrySeed();
    });

    /// <summary>
    /// Memberi tahu UI siapa panel ini dan apa yang boleh dilakukannya.
    ///
    /// Wewenang dikirim supaya tombol yang tidak boleh dipakai dapat
    /// disembunyikan — kenyamanan, bukan pengaman. Penegakannya ada di
    /// Superadmin, yang memeriksa ulang setiap permintaan; panel yang hanya
    /// menyembunyikan tombol dapat dilewati siapa pun yang menyusun pesannya
    /// sendiri.
    /// </summary>
    /// <summary>
    /// Alamat IPv4 mesin ini yang dapat dijangkau mesin lain.
    ///
    /// Loopback dibuang karena hanya berarti "mesin ini sendiri". APIPA
    /// (169.254.x) dibuang karena ia justru TANDA jaringan yang gagal —
    /// Windows memberikannya saat DHCP tidak menjawab, dan mesin lain tidak
    /// akan pernah dapat menjangkaunya.
    ///
    /// Adaptor virtual tetap ditampilkan, tidak disembunyikan. Installer
    /// mungkin memang perlu memakainya, dan menyembunyikan alamat yang sah
    /// karena namanya mengandung "VirtualBox" adalah tebakan yang bisa salah.
    /// Yang membacanya tahu mesinnya sendiri; yang ia butuhkan adalah daftar
    /// lengkap, bukan daftar yang sudah disaring atas namanya.
    /// </summary>
    private static string[] ReachableAddresses()
    {
        try
        {
            return [.. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(a => !System.Net.IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Where(a => !a.StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()];
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    private void PushLinkStatus() => PushJson(new
    {
        type = "linkStatus",

        // Tiga keadaan yang berbeda, dikirim terpisah karena menuntut tiga
        // tawaran berbeda di layar: masukkan kode, coba sambungkan lagi, atau
        // tunggu persetujuan. Digabung menjadi satu bool "siap", ketiganya
        // terlihat sama dan orang menebak mana yang berlaku.
        linked = _link.Linked,
        connected = _link.Connected,
        ready = _link.Ready,

        role = _link.Role.ToString(),

        // Daftar wewenang tunggal, bukan hasil ToString() enum bendera —
        // yang mengembalikan "Superadmin" saja dan membuat panel menyimpulkan
        // pemegang wewenang tertinggi tidak boleh melakukan apa pun.
        rights = _link.Rights.ToAtomicNames(),
        labName = _link.LabName,
        hostsLab = _link.IsSuperadminMachine,

        // Seluruh lab yang mengenali panel ini. Dosen yang mengajar di beberapa
        // ruangan melihat semuanya di sini dan tidak perlu mendaftar ulang di
        // mana pun.
        knownLabs = _link.KnownLabs.Select(l => new
        {
            thumbprint = l.Thumbprint,
            labName = l.LabName,
            host = l.Host,
            lastUsed = l.LastUsed,
            current = l.Thumbprint == _link.CoordinatorThumbprint,
        }),
        host = _link.CoordinatorHost,
        port = _link.CoordinatorPort,

        /*
            Alamat yang harus dipakai MESIN LAIN, bukan alamat yang dipakai
            panel ini.

            Panel Superadmin menyambung ke koordinatornya sendiri lewat
            127.0.0.1, dan sampai sekarang layar "Tautan lab" hanya menampilkan
            angka itu. Secara teknis benar — itu memang alamat yang dipakainya —
            dan sepenuhnya salah dibaca oleh orang yang membukanya untuk
            mengetahui apa yang harus diisi di PC mahasiswa.

            127.0.0.1 di PC mahasiswa berarti "mesin ini sendiri". Agent lalu
            mencoba menyambung ke koordinator yang tidak ada di situ, ditolak
            tumpukan TCP-nya sendiri, dan installer melaporkan sambungan yang
            ditolak. Yang membaca layar itu tidak melakukan satu pun kesalahan.

            Jadi mesin yang menjalankan lab menampilkan alamatnya sendiri
            sebagaimana terlihat dari jaringan. Loopback dan APIPA dibuang:
            keduanya tidak pernah dapat dipakai mesin lain, dan menampilkannya
            hanya mengulang kesalahan yang sama dalam bentuk lain.
        */
        labAddresses = _link.IsSuperadminMachine ? ReachableAddresses() : [],
        thumbprint = _link.CoordinatorThumbprint,
        deviceCode = _link.DeviceCode,
        rejectReason = _linkFailure ?? _link.RejectReason,
    });

    private string? _linkFailure;

    /// <summary>
    /// Melaporkan bahwa sambungan gagal dibuat sama sekali.
    ///
    /// Berbeda dari penolakan: penolakan berarti Superadmin menjawab dan
    /// berkata tidak, kegagalan ini berarti tidak ada yang menjawab. Dua sebab
    /// dengan dua tindakan lanjutan yang berbeda — periksa persetujuan, atau
    /// periksa alamat dan kabel — dan menampilkan keduanya sebagai "tidak
    /// tersambung" mengirim orang mencari di tempat yang salah.
    /// </summary>
    public void ReportLinkFailure(string message) => Dispatcher.BeginInvoke(() =>
    {
        _linkFailure = message;
        PushLinkStatus();
    });

    /// <summary>
    /// Menjalankan pekerjaan asinkron dan MEMBUAT KEGAGALANNYA TERLIHAT.
    ///
    /// Bentuk `_ = SesuatuAsync()` menelan pengecualian tanpa jejak apa pun.
    /// Di panel ini akibatnya adalah tombol yang ditekan lalu tidak terjadi
    /// apa-apa — tanpa galat, tanpa baris log, tanpa cara membedakan "gagal"
    /// dari "berhasil tapi tidak ada yang berubah". Itu jenis kegagalan yang
    /// paling mahal dilacak, jadi setiap panggilan lewat sini.
    /// </summary>
    private void Run(Func<Task> work, string what)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Gagal {What}", what);
                await Dispatcher.InvokeAsync(() => PushToast($"Gagal {what}: {ex.Message}", true));
            }
        });
    }

    /// <summary>
    /// Meneruskan satu pratinjau ke UI. Dikirim per unit, bukan digabung
    /// dalam snapshot: 40 gambar base64 sekaligus akan menjadi payload
    /// ratusan kilobyte tiap dua detik.
    /// </summary>
    private void OnThumbnailArrived(string unitId, string dataUrl)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _thumbnails[unitId] = dataUrl;
            if (!_pageLoaded || _web.CoreWebView2 is null) return;

            var payload = new { type = "thumbnail", unitId, dataUrl };
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
        });
    }

    /// <summary>Mengirim jejak audit terbaru ke UI.</summary>
    private void PushActivity()
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;

        var payload = new
        {
            type = "activity",
            entries = _activity.Select(e => new
            {
                unitId = e.UnitId,
                actor = e.Actor,
                action = e.Action,
                detail = e.Detail,
                createdAt = e.CreatedAt,
            }),
        };

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>Mengirim snapshot unit terkini ke UI React.</summary>
    private void PushUnits()
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;

        var payload = new
        {
            type = "units",
            units = _units.Select(u => new
            {
                unitId = u.UnitId,
                alias = u.Alias,
                autoFullscreen = u.AutoFullscreen,
                blocked = u.Blocked,

                // Siapa yang duduk di unit ini. Datanya sudah mengalir dari agent
                // sejak lama dan berhenti di UnitRegistry.ApplyHeartbeat, yang
                // hanya membaca status lalu membuang sisanya.
                studentNim = u.StudentNim,
                studentName = u.StudentName,
                isGuest = u.IsGuest,
                status = u.Status.ToString(),
                ipAddress = u.IpAddress,
                hostName = u.HostName,
                macAddress = u.MacAddress,
                lastSeen = u.LastSeen,

                // Bahan peta jaringan. Semuanya hasil pengukuran: jenis dan
                // kecepatan tautan diukur di unit, latensi diukur pulang-pergi
                // oleh koordinator, dan alamat koordinator adalah alamat yang
                // benar-benar dipakai unit itu untuk sampai.
                wired = u.WiredConnection,
                linkSpeedMbps = u.LinkSpeedMbps,
                networkAdapter = u.NetworkAdapter,
                coordinatorHost = u.CoordinatorHost,
                latencyMs = u.LatencyMs,

                // Pratinjau diambil dari simpanan lokal, bukan dari daftar yang
                // baru tiba. Dorongan berkala sengaja tidak membawanya, jadi
                // memakai u.ThumbnailDataUrl di sini akan mengosongkan seluruh
                // kartu setiap setengah detik — berkedip-kedip tanpa satu pun
                // galat yang menjelaskan sebabnya.
                thumbnail = _thumbnails.GetValueOrDefault(u.UnitId),

                helpRequestedAt = u.HelpRequestedAt,
                unread = u.Unread,
            }),
        };

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>
    /// Menjalankan perintah daya. Menyalakan unit adalah satu-satunya perintah
    /// yang tidak lewat agent — saat unit mati tidak ada agent yang berjalan,
    /// jadi yang dipakai adalah magic packet ke alamat MAC yang tercatat.
    /// </summary>
    private void HandlePower(string type, string json)
    {
        var target = JsonSerializer.Deserialize<UnitTarget>(json, JsonOpts)?.UnitId;
        var actor = _config.Role;

        if (type == "powerOnAll")
        {
            // Magic packet dipancarkan Superadmin, bukan panel ini. Unit yang
            // mati harus dijangkau lewat broadcast subnet lab, dan laptop dosen
            // di Wi-Fi tamu belum tentu berada di subnet itu — dikirim dari
            // sana, paketnya hilang tanpa satu pun tanda kegagalan.
            Run(async () =>
            {
                var sent = await _link.WakeUnitsAsync();
                await Dispatcher.InvokeAsync(() =>
                    PushToast($"Magic packet dikirim ke {sent} unit", sent == 0));
            }, "menyalakan unit");
            return;
        }

        AgentCommand command = type switch
        {
            "powerOff" or "powerOffAll" => new ShutdownCommand { IssuedBy = actor },
            "restart"  or "restartAll"  => new RestartCommand  { IssuedBy = actor },
            _                            => new AbortShutdownCommand { IssuedBy = actor },
        };

        if (type.EndsWith("All", StringComparison.Ordinal))
        {
            Dispatch(command, type);
        }
        else if (target is not null)
        {
            Dispatch(command, "power." + type, [target], $"Perintah dikirim ke {target}");
        }
    }

    /// <summary>
    /// Mengirim satu perintah lewat Superadmin.
    ///
    /// Pengganti BeginBatch dan seluruh pasangan ConnectionIdFor + SendAsync
    /// yang tersebar di jendela ini. Pencarian tujuan, pelacakan batch, dan
    /// pencatatan audit tidak lagi dikerjakan di sini — ketiganya dilakukan
    /// Superadmin, sekali, untuk semua panel. Yang tersisa di sini hanyalah
    /// menyampaikan hasilnya kepada orang yang menekan tombolnya.
    /// </summary>
    private void Dispatch(AgentCommand command, string action,
                          string[]? targets = null, string? successToast = null)
    {
        Run(async () =>
        {
            var result = await _link.DispatchAsync(command, action, targets);

            await Dispatcher.InvokeAsync(() =>
            {
                if (!result.Accepted)
                {
                    PushToast(result.RejectReason ?? "Perintah ditolak", true);
                    return;
                }

                // String kosong berarti pemanggil menampilkan pesannya sendiri.
                // Dibedakan dari null, yang berarti "pakai kalimat bawaan".
                if (successToast is "") return;

                PushToast(successToast
                    ?? $"Perintah dikirim ke {result.Reached} unit · menunggu balasan");
            });
        }, action);
    }

    /// <summary>
    /// Perintah kunci layar. Agent meneruskannya ke RetrivaClient di sesi
    /// pengguna, karena layanan di Session 0 tidak dapat menampilkan jendela.
    /// </summary>
    private void HandleLock(string type, string json)
    {
        var target = JsonSerializer.Deserialize<UnitTarget>(json, JsonOpts)?.UnitId;
        var actor = _config.Role;
        var locking = type is "lock" or "lockAll";

        AgentCommand command = locking
            ? new LockScreenCommand { IssuedBy = actor }
            : new UnlockScreenCommand { IssuedBy = actor };

        if (type.EndsWith("All", StringComparison.Ordinal))
        {
            Dispatch(command, type);
        }
        else if (target is not null)
        {
            Dispatch(command, locking ? "screen.lock" : "screen.unlock", [target],
                     locking ? $"{target} dikunci" : $"{target} dibuka");
        }
    }

    private void OnBatchCompleted(BatchSummary summary)
    {
        if (summary.FailedUnits.Count > 0)
            _log.LogWarning("{Action} gagal di: {Units}", summary.Action,
                string.Join(", ", summary.FailedUnits));

        if (summary.SilentUnits.Count > 0)
            _log.LogWarning("{Action} tidak dijawab: {Units}", summary.Action,
                string.Join(", ", summary.SilentUnits));

        Dispatcher.BeginInvoke(() =>
        {
            PushToast(summary.Describe(), !summary.AllGood);
            PushBatchResult(summary);
        });
    }

    private void PushBatchResult(BatchSummary summary)
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;

        var payload = new
        {
            type = "batchResult",
            action = summary.Action,
            expected = summary.Expected,
            succeeded = summary.Succeeded,
            failed = summary.Failed,
            noReply = summary.NoReply,
            failedUnits = summary.FailedUnits,
            silentUnits = summary.SilentUnits,
        };

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>
    /// Mengirim daftar layar dan jendela yang benar-benar dapat disiarkan.
    ///
    /// Sebelumnya tombol sumber tampilan hanya hiasan — apa pun yang dipilih,
    /// yang tertangkap tetap layar utama. Daftar ini membuat pilihannya nyata.
    /// </summary>
    private void PushCaptureSources()
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;

        _captureTargets.Clear();
        _captureTargets.AddRange(CaptureTargets.Monitors());
        _captureTargets.AddRange(CaptureTargets.Windows());

        var payload = new
        {
            type = "captureSources",
            sources = _captureTargets.Select((t, i) => new
            {
                index = i,
                kind = t.Kind.ToString(),
                label = t.Label,
                size = t.Width > 0 ? $"{t.Width}x{t.Height}" : "layar utama",
            }),
        };

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private readonly List<CaptureTarget> _captureTargets = [];

    /*
        Pratinjau sumber, berjalan SEBELUM siaran dimulai.

        Tanpa ini, pemilih sumber tampilan meminta orang memilih tanpa melihat:
        nama window saja tidak cukup untuk tahu mana yang berisi slide dan mana
        yang berisi catatan pribadi. Menyalakan siaran dulu untuk memeriksanya
        berarti seluruh kelas terlanjur melihat apa pun yang salah dipilih.

        Ditangkap pada resolusi ASLI, tanpa penyusutan.

        Versi pertama memakai 640 piksel supaya murah, dan itu keliru: layar
        1920 yang dikecilkan tiga kali membuat teks tidak terbaca, sehingga
        pratinjaunya gagal melakukan satu-satunya tugasnya — membedakan jendela
        berisi slide dari jendela berisi catatan pribadi. Pratinjau yang tidak
        dapat dibaca sama tidak bergunanya dengan tidak ada pratinjau.

        Ongkosnya ditebus dari laju, bukan dari ketajaman: dua frame per detik.
        Memilih sumber adalah tindakan lambat dan disengaja — tidak ada yang
        perlu dilihat bergerak mulus di sini.
    */
    private GdiScreenSource? _sourcePreview;

    private readonly System.Windows.Threading.DispatcherTimer _sourcePreviewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    private void HandleSourcePreview(string type, string json)
    {
        StopSourcePreview();
        if (type == "previewStop") { PushJson(new { type = "broadcastPreview" }); return; }

        // Siaran yang sedang berjalan punya pratinjaunya sendiri dari frame yang
        // benar-benar dikirim. Menimpanya dengan tangkapan terpisah akan
        // menampilkan sesuatu yang belum tentu sama dengan yang diterima kelas.
        if (_broadcast?.Running == true) return;

        var request = JsonSerializer.Deserialize<BroadcastRequest>(json, JsonOpts);
        var target = request?.SourceIndex is { } index
                  && index >= 0 && index < _captureTargets.Count
            ? _captureTargets[index]
            : CaptureTarget.PrimaryScreen();

        if (target.Kind == CaptureKind.Window
            && CaptureTargets.IsMinimized(target.WindowHandle))
        {
            PushJson(new { type = "sourcePreviewNote", note = $"{target.Label} sedang diminimalkan" });
            return;
        }

        try
        {
            // 3840 hanyalah batas atas: GdiScreenSource membatasinya pada lebar
            // sumber, jadi angka ini menghasilkan resolusi asli untuk layar mana
            // pun yang wajar. Kualitas 92 setara pilihan "Tinggi" pada siaran.
            _sourcePreview = new GdiScreenSource(3840, 92, target);
            _sourcePreviewTimer.Start();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.LogWarning("Pratinjau sumber gagal: {Message}", ex.Message);
        }
    }

    private void PushSourcePreview()
    {
        if (_sourcePreview is null) return;

        var frame = _sourcePreview.Grab();
        if (frame is null) return;

        PushJson(new
        {
            type = "broadcastPreview",
            dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(frame.Data),
        });
    }

    private void StopSourcePreview()
    {
        _sourcePreviewTimer.Stop();
        _sourcePreview?.Dispose();
        _sourcePreview = null;
    }

    /// <summary>Mengatur siaran layar: mulai, hentikan, atau bekukan.</summary>
    private void HandleBroadcast(string type, string json)
    {
        var request = JsonSerializer.Deserialize<BroadcastRequest>(json, JsonOpts);

        switch (type)
        {
            case "broadcastStart":
            {
                var width = request?.Resolution switch { 480 => 854, 1080 => 1920, _ => 1280 };
                var fps = request?.Fps ?? 15;

                /*
                    Kualitas JPEG ditentukan dari panel, bukan dipatok di kode.

                    Batas atasnya 98, bukan 100. Di atas sekitar 95, JPEG hampir
                    tidak menambah detail yang terlihat sementara ukurannya naik
                    tajam — pada 100 ia bahkan berhenti membuang informasi sama
                    sekali dan berkasnya membengkak berkali lipat untuk perbedaan
                    yang tidak dapat dilihat mata di layar proyektor.
                */
                var quality = Math.Clamp(request?.Quality ?? 80, 40, 98);

                var target = request?.SourceIndex is { } index
                          && index >= 0 && index < _captureTargets.Count
                    ? _captureTargets[index]
                    : CaptureTarget.PrimaryScreen();

                // Jendela yang diminimalkan tidak punya piksel untuk diserahkan.
                // Diberitahukan di depan, karena gejalanya di sisi klien hanya
                // bidang hitam — yang tidak menjelaskan apa-apa.
                if (target.Kind == CaptureKind.Window
                    && CaptureTargets.IsMinimized(target.WindowHandle))
                {
                    PushToast($"{target.Label} sedang diminimalkan — buka jendelanya lalu mulai lagi", true);
                    return;
                }

                /*
                    IZIN DIMINTA LEBIH DULU, SEBELUM SATU FRAME PUN DIKIRIM.

                    Superadmin yang menegakkan aturan satu sumber pada satu
                    waktu, dan ia yang memberi tahu ke mana aliran harus
                    dikirim. Memulai penangkapan lebih dulu lalu meminta izin
                    berarti dua panel dapat sama-sama menangkap layar selama
                    sepersekian detik — dan yang kalah baru berhenti setelah
                    sempat mengirim.
                */
                var label = target.Label;

                Run(async () =>
                {
                    var grant = await _link.ClaimBroadcastAsync(new BroadcastClaim
                    {
                        Width = width,
                        FrameRate = fps,
                        Quality = quality,
                        SourceLabel = label,
                        Targets = request?.Targets,
                        Takeover = request?.Takeover ?? false,
                    });

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!grant.Granted)
                        {
                            PushToast(grant.HeldBy is { } who
                                ? $"{who} sedang menyiarkan. Hentikan siarannya dulu."
                                : grant.Warning ?? "Siaran ditolak", true);
                            PushJson(new { type = "broadcastEnded", reason = (string?)null });
                            return;
                        }

                        if (grant.Warning is { } warn) PushToast(warn, true);

                        /*
                            Frame dikirim UNICAST ke Superadmin, bukan multicast
                            ke lab.

                            Panel di Wi-Fi yang memancarkan multicast memaksa
                            access point menyiarkan ulang seluruh aliran ke
                            udara — termasuk kembali ke laptop yang mengirimnya.
                            Superadmin duduk di kabel gigabit dan memancarkannya
                            dari sana, tempat multicast memang bekerja.
                        */
                        _ = _broadcast?.DisposeAsync();
                        _broadcast = new BroadcastSession(new BroadcastSettings(
                            TargetWidth: width, Quality: quality, Fps: fps,
                            Port: grant.RelayPort, Target: target,
                            UnicastHost: grant.RelayHost));

                        /*
                            Penurunan kualitas DIBERITAHUKAN, tidak terjadi
                            diam-diam.

                            Dosen yang mendapati gambarnya memburuk tanpa
                            keterangan akan menyimpulkan aplikasinya rusak, lalu
                            mengulang siaran berkali-kali mencari sebab yang tidak
                            akan pernah ia temukan. Satu pemberitahuan mengubahnya
                            menjadi keputusan yang dapat ia pahami — dan bila perlu,
                            ia dapat menutup video atau menyambungkan kabel.
                        */
                        _broadcast.QualityChanged += (q, mbps) =>
                            Dispatcher.BeginInvoke(() =>
                            {
                                _log.LogInformation(
                                    "Kualitas siaran disesuaikan ke q{Quality} pada {Mbps:F1} Mbps",
                                    q, mbps);

                                PushToast(
                                    $"Kualitas siaran diturunkan ke {q} — laju {mbps:F0} Mbps "
                                  + "melewati anggaran jaringan lab");
                            });

                        StopSourcePreview();
                        _broadcast.Start();
                        _previewTimer.Start();

                        // Perintah membuka penonton disebar ke seluruh unit.
                        // Byte video TIDAK lewat jalur ini — hanya perintahnya.
                        // Videonya mengalir lewat relay, dan dari sana multicast.
                        Dispatch(new StartBroadcastCommand
                        {
                            MulticastGroup = FramePacket.MulticastGroup,
                            Port = FramePacket.DefaultPort,
                            SourceLabel = label,
                            Width = width,
                            FrameRate = fps,
                        }, "broadcast.start", request?.Targets, successToast: "");

                        _log.LogInformation(
                            "Siaran dimulai: {Target} [{Kind}] · tangkap {Rect} via {Method} · "
                          + "keluar {Width}px q{Q} {Fps} fps ke relay {Relay}:{Port}",
                            target.Label, target.Kind, _broadcast.CaptureRect,
                            _broadcast.CaptureMethod, width, quality, fps,
                            grant.RelayHost, grant.RelayPort);

                        PushJson(new
                        {
                            type = "broadcastInfo",
                            target = target.Label,
                            kind = target.Kind.ToString(),
                        });
                        PushToast($"Menyiarkan: {target.Label}");
                    });
                }, "memulai siaran");
                break;
            }

            case "broadcastUnit":
            {
                /*
                    Menyiarkan layar SEORANG MAHASISWA ke seluruh kelas.

                    Panel tidak menangkap apa pun di sini. Ia mengambil hak
                    siaran, lalu menyuruh unit sumber mengalirkan layarnya ke
                    relay — jalur yang sama persis dengan siaran dosen, hanya
                    pangkalnya yang berbeda. Sesudah itu unit LAIN diminta
                    membuka penontonnya.

                    Unit sumber sengaja tidak ikut membuka penonton: ia sedang
                    melihat layarnya sendiri secara langsung, dan menampilkan
                    salinan siarannya di atas itu hanya menutupi apa yang sedang
                    ia jelaskan.
                */
                if (request?.UnitId is not { Length: > 0 } presenter)
                {
                    PushToast("Belum ada unit yang dipilih", true);
                    return;
                }

                var who = _link.Units.FirstOrDefault(u => u.UnitId == presenter);
                var label = who?.StudentName is { Length: > 0 } n
                    ? $"{n} · {who.Alias ?? presenter}"
                    : who?.Alias ?? presenter;

                Run(async () =>
                {
                    // Ukuran dan laju diputuskan di sini, bukan diambil dari
                    // pengaturan siaran dosen: yang menangkap adalah PC
                    // mahasiswa, yang biasanya lebih lemah dan sering di Wi-Fi.
                    var grant = await _link.ClaimBroadcastAsync(new BroadcastClaim
                    {
                        Width = 1600,
                        FrameRate = 15,
                        Quality = 78,
                        SourceLabel = label,
                        Takeover = request.Takeover ?? false,
                    });

                    if (!grant.Granted)
                    {
                        await Dispatcher.InvokeAsync(() => PushToast(grant.HeldBy is { } held
                            ? $"{held} sedang menyiarkan. Hentikan siarannya dulu."
                            : grant.Warning ?? "Siaran ditolak", true));
                        return;
                    }

                    await _link.DispatchAsync(new StartPresentingCommand
                    {
                        RelayHost = grant.RelayHost ?? PanelProtocol.LoopbackHost,
                        RelayPort = grant.RelayPort,
                    }, "present.start", [presenter]);

                    var viewers = _link.Units
                        .Where(u => u.Status != UnitStatus.Offline && u.UnitId != presenter)
                        .Select(u => u.UnitId)
                        .ToArray();

                    if (viewers.Length > 0)
                    {
                        await _link.DispatchAsync(new StartBroadcastCommand
                        {
                            MulticastGroup = FramePacket.MulticastGroup,
                            Port = FramePacket.DefaultPort,
                            SourceLabel = label,
                        }, "broadcast.start", viewers);
                    }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        _presentingUnit = presenter;

                        // Panel ikut bergabung ke grup multicast supaya kotak
                        // pratinjau menampilkan siaran yang sebenarnya. Tanpa
                        // ini kotaknya kosong — dan dosen tidak punya cara
                        // membedakan "belum berjalan" dari "berjalan, tapi
                        // tidak ada tempat melihatnya".
                        _presentWatch?.Dispose();
                        _presentWatch = new PresentWatcher();
                        _previewTimer.Start();

                        PushJson(new { type = "broadcastInfo", target = label, kind = "Student" });

                        // Nol penonton disebut apa adanya, bukan ditulis
                        // "ke 0 unit" dan dibiarkan terbaca sebagai kegagalan.
                        // Unit sumber memang tidak menonton dirinya sendiri;
                        // saat ia satu-satunya unit online, tidak ada yang
                        // rusak — hanya tidak ada penonton.
                        PushToast(viewers.Length == 0
                            ? $"Layar {label} disiarkan, tetapi belum ada unit lain yang menonton"
                            : $"Menyiarkan layar {label} ke {viewers.Length} unit");
                    });
                }, "menyiarkan layar mahasiswa");
                break;
            }

            case "broadcastStop":
            {
                EndBroadcast();
                break;
            }

            case "broadcastFreeze":
            {
                if (_broadcast is null) { PushToast("Siaran belum berjalan", true); return; }
                _broadcast.Frozen = request?.Frozen ?? !_broadcast.Frozen;
                var frozen = _broadcast.Frozen;
                Run(() => _link.WriteActivityAsync("broadcast.freeze",
                        detail: frozen ? "dibekukan" : "dilanjutkan"),
                    "mencatat pembekuan siaran");
                PushToast(frozen
                    ? "Tampilan dibekukan — siaran tetap berjalan"
                    : "Siaran dilanjutkan");
                break;
            }
        }
    }

    /// <summary>Mengirim frame terakhir yang benar-benar disiarkan ke UI.</summary>
    private void PushBroadcastPreview(bool clear = false)
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;

        /*
            Presentasi mahasiswa: pratinjau diambil dari SIARANNYA, bukan dari
            tangkapan lokal — karena panel ini tidak menangkap apa pun.

            Tanpa ini, dosen yang menyuruh mahasiswa presentasi melihat kotak
            pratinjau kosong dan tidak punya cara mengetahui apakah layarnya
            benar-benar terkirim. Yang tampil di sini persis yang diterima kelas:
            frame yang keluar dari relay, bukan tebakan tentangnya.
        */
        if (!clear && _broadcast is null && _presentWatch is { } watch)
        {
            var seen = watch.LastFrame;

            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "broadcastPreview",
                dataUrl = seen.Length == 0
                    ? null
                    : "data:image/jpeg;base64," + Convert.ToBase64String(seen),
                // Angka yang DIUKUR di sisi ini, bukan disalin dari sisi
                // pengirim. Panel tidak menangkap apa pun, jadi "frame terkirim"
                // dan "Mbps keluar" tidak punya arti di sini; yang punya arti
                // adalah berapa yang benar-benar tiba.
                watching = new
                {
                    framesSeen = watch.Frames,
                    packetsSeen = watch.Packets,
                },
            }, JsonOpts));
            return;
        }

        if (clear || _broadcast is null)
        {
            _web.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(new { type = "broadcastPreview" }, JsonOpts));
            return;
        }

        // Siaran bisa gugur sendiri di tengah jalan — jendela target ditutup,
        // misalnya. Denyut pratinjau adalah tempat paling wajar untuk
        // menyadarinya, karena inilah satu-satunya yang berjalan berkala.
        if (_broadcast.Fault is { } fault)
        {
            EndBroadcast(fault);
            return;
        }

        var frame = _broadcast.LastFrame;
        var status = _broadcast.Status();

        var payload = new
        {
            type = "broadcastPreview",
            dataUrl = frame.Length == 0
                ? null
                : "data:image/jpeg;base64," + Convert.ToBase64String(frame),
            stats = new
            {
                framesSent = status.FramesSent,
                packetsSent = status.PacketsSent,
                bytesSent = status.BytesSent,
                sendErrors = status.SendErrors,
                actualFps = status.ActualFps,
                frozen = status.Frozen,

                // Persegi dan cara tangkap ikut tiap denyut, bukan sekali di awal.
                // Cara tangkap baru diketahui setelah frame pertama diambil, dan
                // bisa berubah di tengah siaran bila PrintWindow mulai gagal.
                rect = _broadcast.CaptureRect,
                method = _broadcast.CaptureMethod,
                minimized = _broadcast.TargetMinimized,
            },
        };

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>
    /// Memilih, menitipkan, dan membagikan berkas.
    ///
    /// Panel TIDAK mengirim isi berkas ke agent. Ia menitipkan salinan ke lemari
    /// Superadmin, lalu menyuruh tiap agent mengunduhnya sendiri. Yang menyeberang
    /// jaringan dari sini hanyalah perintah berisi alamat dan sidik jari.
    /// </summary>
    private void HandleFiles(string type, string json)
    {
        var request = JsonSerializer.Deserialize<FileRequest>(json, JsonOpts);

        switch (type)
        {
            case "filePick":
            {
                // Dialog Windows asli, bukan daftar palsu di halaman web.
                // WebView2 tidak dapat membaca path berkas dari sisi web, dan
                // tanpa path yang sesungguhnya tidak ada yang bisa disalin.
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Pilih berkas untuk dibagikan",
                    Multiselect = true,
                    CheckFileExists = true,
                };

                if (dialog.ShowDialog(this) != true) return;

                var picked = dialog.FileNames
                    .Where(File.Exists)
                    .Select(path => new
                    {
                        path,
                        name = Path.GetFileName(path),
                        size = new FileInfo(path).Length,
                    })
                    .ToList();

                PushJson(new { type = "filesPicked", files = picked });
                break;
            }

            case "fileSend":
            {
                if (request?.Paths is not { Length: > 0 } paths)
                {
                    PushToast("Belum ada berkas yang dipilih", true);
                    return;
                }

                /*
                    BATAS YANG DIKETAHUI DAN SENGAJA DIBUAT TERLIHAT.

                    Lemari berkas ada di mesin Superadmin, dan agent mengunduh
                    dari titik HTTP-nya. Panel yang berjalan di laptop terpisah
                    dapat menitipkan berkas ke lemari lokalnya sendiri — dan
                    agent tidak akan pernah menemukannya di sana.

                    Gejalanya berupa "Membagikan ke 40 unit" yang terlihat
                    berhasil, diikuti empat puluh unduhan yang gagal diam-diam.
                    Sampai jalur unggah panel ke Superadmin ada, penolakannya
                    dinyatakan di depan.
                */
                if (_link.Role != PanelRole.Superadmin)
                {
                    PushToast("Berbagi berkas baru tersedia di panel Superadmin", true);
                    return;
                }

                var targets = _units
                    .Where(u => u.Status != UnitStatus.Offline)
                    .Where(u => request.Targets is not { Length: > 0 }
                             || request.Targets.Contains(u.UnitId))
                    .ToList();

                if (targets.Count == 0)
                {
                    PushToast("Tidak ada unit yang tersambung", true);
                    return;
                }

                var unitIds = targets.Select(u => u.UnitId).ToArray();
                var queue = paths.Where(File.Exists).ToArray();

                /*
                    PENITIPAN BERJALAN DI LUAR UTAS UI, dan itu diperbaiki
                    setelah panel benar-benar membeku.

                    Versi sebelumnya memanggil _vault.Stage(path) langsung di
                    sini. Fungsi itu menyalin berkasnya lalu membacanya ulang
                    untuk dihitung sidiknya — dan blok ini dijalankan penangan
                    pesan WebView, yang berada di utas UI.

                    Untuk berkas 1-200 MB pekerjaannya selesai dalam sepersekian
                    detik dan tidak ada yang terlihat. Untuk berkas 7 GB, utas
                    UI berhenti melayani apa pun selama menit-menitan: jendela
                    tidak menggambar ulang, tombol tidak menjawab, dan Windows
                    menempelkan label "Not Responding" pada panel Superadmin di
                    tengah kelas berjalan.

                    Sekarang Run() memindahkannya ke kolam utas, dan kemajuannya
                    dikabarkan ke halaman selagi berlangsung. Panel tetap dapat
                    dipakai — siaran, obrolan, dan kendali jarak jauh terus
                    berjalan sementara berkasnya disalin.
                */
                Run(async () =>
                {
                    foreach (var path in queue)
                    {
                        var name = Path.GetFileName(path);

                        await Dispatcher.InvokeAsync(() => PushJson(new
                        {
                            type = "fileStaging",
                            sourcePath = path,
                            fileName = name,
                            copied = 0L,
                            total = new FileInfo(path).Length,
                        }));

                        StagedFile staged;
                        try
                        {
                            staged = await _vault.StageAsync(path, (copied, total) =>
                                Dispatcher.InvokeAsync(() => PushJson(new
                                {
                                    type = "fileStaging",
                                    sourcePath = path,
                                    fileName = name,
                                    copied,
                                    total,
                                })));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _log.LogWarning("Gagal menitipkan {Path}: {Message}", path, ex.Message);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                PushJson(new { type = "fileStagingFailed", sourcePath = path });
                                PushToast($"Gagal membaca {name} · {ex.Message}", true);
                            });
                            continue;
                        }

                        var command = new PushFileCommand
                        {
                            IssuedBy = _config.Role,
                            TransferId = staged.Id,
                            FileName = staged.FileName,
                            SizeBytes = staged.SizeBytes,
                            DownloadUrl = $"{FileEndpoints.Path}/{staged.Id}",
                            Sha256 = staged.Sha256,
                        };

                        // Dikirim lewat Dispatch, tetapi hasil batch-nya diabaikan.
                        //
                        // Unduhan berjalan menit-menitan sedangkan pelacak batch
                        // menyerah setelah 12 detik. Kemajuan sebenarnya dilaporkan
                        // agent lewat FileProgress, jadi laporan batch di sini
                        // hanya akan mengabarkan kegagalan yang tidak terjadi.
                        Run(async () => await _link.DispatchAsync(command, "file.push", unitIds),
                            $"membagikan {staged.FileName}");

                        await Dispatcher.InvokeAsync(() => PushJson(new
                        {
                            type = "fileStarted",
                            transferId = staged.Id,

                            /*
                                Jalur ASAL ikut dikirim, dan inilah yang
                                mengosongkan antrean pilihan.

                                Halaman menyimpan daftar berkas terpilih dengan
                                kunci berupa path. Tanpa path di pesan ini,
                                halaman tidak punya cara mengetahui berkas
                                terpilih yang MANA yang barusan berangkat, jadi
                                daftarnya tidak pernah menyusut — dan pembagian
                                berikutnya mengirim ulang semua yang sudah
                                terkirim, tanpa satu pun tanda bahwa itu terjadi.
                            */
                            sourcePath = path,
                            fileName = staged.FileName,
                            sizeBytes = staged.SizeBytes,
                            units = unitIds,
                        }));
                    }

                    await Dispatcher.InvokeAsync(() =>
                        PushToast($"Membagikan ke {unitIds.Length} unit"));
                }, "menitipkan berkas ke lemari");

                break;
            }

            case "fileClear":
            {
                var (removed, stuck) = _vault.Clear();

                Run(() => _link.WriteActivityAsync("file.cleared",
                        detail: stuck == 0
                            ? $"{removed} berkas dibuang"
                            : $"{removed} dibuang, {stuck} GAGAL dan masih dapat diunduh"),
                    "mencatat pembersihan lemari");

                PushJson(new { type = "filesReset" });

                // Kegagalan disebutkan, bukan disembunyikan di balik angka
                // sukses. Asisten yang mengira lemarinya kosong padahal soal
                // ujian masih di sana tidak punya cara mengetahuinya.
                PushToast(stuck == 0
                    ? $"{removed} berkas dibuang dari lemari"
                    : $"{removed} dibuang — {stuck} GAGAL dihapus dan masih dapat diunduh", stuck > 0);
                break;
            }
        }
    }

    private void OnFileProgress(FileTransferProgress progress) => Dispatcher.Invoke(() =>
        PushJson(new
        {
            type = "fileProgress",
            unitId = progress.UnitId,
            transferId = progress.TransferId,
            fileName = progress.FileName,
            percent = progress.Percent,
            status = progress.Status,
            detail = progress.Detail,
        }));

    /// <summary>Chat dua arah dan penanganan permintaan bantuan.</summary>
    private void HandleChat(string type, string json)
    {
        var request = JsonSerializer.Deserialize<ChatRequest>(json, JsonOpts);
        var sender = _config.Role == "Primary" ? "PC Asisten" : "PC Dosen";

        switch (type)
        {
            case "chatOpen":
            {
                // Membuka percakapan menandainya sudah dibaca. Penanda "belum
                // dibaca" yang tidak pernah padam akan diabaikan orang dalam
                // seminggu, dan setelah itu ia tidak menandakan apa pun lagi.
                if (request?.UnitId is not { } unitId) return;

                // Penandaan sudah dibaca dilakukan Superadmin di dalam
                // pengambilan riwayat — satu perjalanan, bukan dua, dan tidak
                // ada celah di mana riwayat terkirim tetapi penandanya tidak.
                Run(async () =>
                {
                    var messages = await _link.GetChatHistoryAsync(unitId);

                    await Dispatcher.InvokeAsync(() => PushJson(new
                    {
                        type = "chatHistory",
                        unitId,
                        messages = messages.Select(m => new
                        {
                            fromAdmin = m.FromAdmin,
                            displayName = m.DisplayName,
                            text = m.Text,
                            sentAt = m.SentAt,
                        }),
                    }));
                }, $"memuat percakapan {unitId}");
                break;
            }

            case "chatSend":
            {
                var text = request?.Text?.Trim();
                if (string.IsNullOrEmpty(text)) return;

                var toEveryone = request?.ToEveryone ?? false;
                var unitTarget = request?.UnitId;
                if (!toEveryone && unitTarget is null) return;

                // Penyimpanan riwayat dan pengiriman perintah dilakukan
                // Superadmin dalam satu langkah. Dipisah menjadi dua panggilan
                // dari sini, panel yang putus di antaranya meninggalkan pesan
                // yang tersimpan tetapi tidak pernah sampai — atau sebaliknya.
                Run(async () =>
                {
                    var result = await _link.SendChatAsync(unitTarget, text, toEveryone);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!result.Sent)
                        {
                            PushToast(result.RejectReason ?? "Pesan tidak terkirim", true);
                            return;
                        }

                        /*
                            Pesannya TIDAK digambar sendiri di sini.

                            Dulu panel menambahkan pesannya ke percakapan secara
                            lokal, tanpa menunggu koordinator. Terlihat cepat,
                            tetapi menghasilkan dua kebohongan: pengumuman tidak
                            pernah masuk percakapan siapa pun karena cabang ini
                            hanya menangani chat per-unit, dan panel lain tidak
                            pernah tahu asisten sudah menjawab mahasiswa yang
                            sama.

                            Sekarang koordinator mengedarkannya ke seluruh panel
                            lewat jalur yang sama dengan pesan mahasiswa, dan
                            panel ini menerimanya seperti panel lain. Satu
                            sumber, satu percakapan.
                        */
                        if (toEveryone)
                            PushToast($"Pengumuman terkirim ke {result.Reached} unit");
                    });
                }, "mengirim pesan");
                break;
            }

            case "chatClear":
            {
                // Penghapusan di Superadmin dan penyebaran perintah ke seluruh
                // unit dikerjakan bersama di sana. Layar mahasiswa berikutnya
                // adalah PC fisik yang sama; menghapus hanya di satu sisi
                // meninggalkan percakapan lama terpampang di tempat yang paling
                // mudah dibaca orang yang tidak berhak.
                Run(async () =>
                {
                    var removed = await _link.ClearChatAsync();
                    _log.LogInformation("Percakapan direset: {Count} pesan dihapus", removed);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        _pendingHelpUnits.Clear();
                        _units = _units.Select(u => u with { HelpRequestedAt = null }).ToArray();
                        ClearHelpTaskbar();
                        PushJson(new { type = "chatReset" });
                        PushToast($"{removed} pesan dihapus — sesi kelas direset");
                    });
                }, "menghapus percakapan");
                break;
            }

            case "helpAck":
            {
                if (request?.UnitId is not { } unitId) return;
                _pendingHelpUnits.Remove(unitId);
                _units = _units.Select(u => string.Equals(u.UnitId, unitId, StringComparison.OrdinalIgnoreCase)
                    ? u with { HelpRequestedAt = null }
                    : u).ToArray();
                SyncHelpTaskbar();
                Run(() => _link.AcknowledgeHelpAsync(unitId), $"menandai bantuan {unitId}");
                break;
            }
        }
    }

    /// <summary>
    /// Pesan chat baru, dari mahasiswa MAUPUN dari admin.
    ///
    /// FromAdmin diteruskan apa adanya, tidak dipatok false.
    ///
    /// Dulu jalur ini hanya dilalui pesan mahasiswa, jadi nilainya dituliskan
    /// mati sebagai false — benar pada saat ditulis. Begitu pesan admin ikut
    /// diedarkan lewat jalur yang sama, setiap pesan yang baru tiba digambar
    /// sebagai pesan mahasiswa: gelembungnya di kiri, sejajar dengan pesan
    /// orang lain.
    ///
    /// Yang membuatnya sulit dikenali adalah ia memperbaiki dirinya sendiri.
    /// Berpindah tab memuat ulang percakapan dari riwayat, tempat FromAdmin
    /// tersimpan benar — sehingga gelembungnya melompat ke kanan dan tampak
    /// seperti kedipan tampilan, bukan seperti nilai yang salah.
    /// </summary>
    private void OnChatArrived(ChatMessage message) => Dispatcher.Invoke(() =>
    {
        PushJson(new
        {
            type = "chatMessage",
            unitId = message.UnitId,
            fromAdmin = message.FromAdmin,
            displayName = message.DisplayName,
            text = message.Text,
            sentAt = message.SentAt,
        });
    });

    private void OnHelpChanged(HelpRequest request) => Dispatcher.Invoke(() =>
    {
        if (request.Raised)
        {
            _pendingHelpUnits.Add(request.UnitId);
            var who = FormatClientName(request.UnitId, request.DisplayName);

            PushToast($"{who} meminta bantuan", true);
            ShowExternalHelpNotification(who);
            SetHelpTaskbar(flash: true);
        }
        else
        {
            _pendingHelpUnits.Remove(request.UnitId);
            _units = _units.Select(u => string.Equals(u.UnitId, request.UnitId, StringComparison.OrdinalIgnoreCase)
                ? u with { HelpRequestedAt = null }
                : u).ToArray();
            SyncHelpTaskbar();
        }
    });

    /* ================= notifikasi bantuan & taskbar ================= */

    /// <summary>
    /// Menentukan nama tampilan client untuk notifikasi:
    /// Menggunakan nama/nomor PC terdaftar (mis. PC-01) dan username jika ada (mis. PC-01 (ahmad)).
    /// </summary>
    private string FormatClientName(string unitId, string? displayName)
    {
        var unit = _units.FirstOrDefault(u => string.Equals(u.UnitId, unitId, StringComparison.OrdinalIgnoreCase));

        // 1. Nomor / Nama PC terdaftar:
        var pcLabel = !string.IsNullOrWhiteSpace(unit?.Alias) ? unit.Alias.Trim() : unitId.Trim();

        // 2. Username mahasiswa / login client:
        var userLabel = !string.IsNullOrWhiteSpace(unit?.SelfName)
            ? unit.SelfName.Trim()
            : !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : null;

        if (string.Equals(userLabel, pcLabel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(userLabel, unitId, StringComparison.OrdinalIgnoreCase))
        {
            userLabel = null;
        }

        return !string.IsNullOrWhiteSpace(userLabel)
            ? $"{pcLabel} ({userLabel})"
            : pcLabel;
    }

    private void ShowExternalHelpNotification(string clientName)
    {
        try
        {
            var escapedTitle = System.Security.SecurityElement.Escape("Retriva — Permintaan Bantuan");
            var escapedMessage = System.Security.SecurityElement.Escape($"{clientName} meminta bantuan asisten!");
            var xmlContent = $@"
<toast scenario=""reminder"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{escapedTitle}</text>
      <text>{escapedMessage}</text>
    </binding>
  </visual>
  <audio src=""ms-winsoundevent:Notification.Default"" />
</toast>";

            var xml = new XmlDocument();
            xml.LoadXml(xmlContent);

            var toast = new ToastNotification(xml);
            toast.Activated += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (WindowState == WindowState.Minimized)
                            WindowState = WindowState.Normal;
                        Activate();
                        Topmost = true;
                        Topmost = false;
                        Focus();
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Gagal mengaktifkan jendela dari klik toast");
                    }
                });
            };

            var notifier = ToastNotificationManager.CreateToastNotifier("Retriva.Admin");
            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gagal memunculkan notifikasi Windows Toast untuk bantuan");
        }
    }

    private void SyncHelpTaskbar()
    {
        try
        {
            var activeInSnapshot = _units.Where(u => u.HelpRequestedAt != null)
                                        .Select(u => u.UnitId)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Selaraskan antrean lokal dengan status server
            _pendingHelpUnits.IntersectWith(activeInSnapshot);
            foreach (var id in activeInSnapshot)
            {
                _pendingHelpUnits.Add(id);
            }

            if (_pendingHelpUnits.Count > 0)
            {
                SetHelpTaskbar(flash: false);
            }
            else
            {
                ClearHelpTaskbar();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gagal menyelaraskan status taskbar bantuan");
        }
    }

    private void SetHelpTaskbar(bool flash = true)
    {
        try
        {
            TaskbarItemInfo ??= new TaskbarItemInfo();
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Error;
            TaskbarItemInfo.ProgressValue = 1.0;
            TaskbarItemInfo.Overlay = GetOrCreateHelpBadge();

            if (flash) FlashWindow();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gagal mengatur status merah taskbar");
        }
    }

    private void ClearHelpTaskbar()
    {
        try
        {
            _pendingHelpUnits.Clear();
            if (TaskbarItemInfo != null)
            {
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                TaskbarItemInfo.ProgressValue = 0;
                TaskbarItemInfo.Overlay = null;
            }
            StopFlash();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gagal membersihkan status merah taskbar");
        }
    }

    private static ImageSource GetOrCreateHelpBadge()
    {
        if (_helpBadge != null) return _helpBadge;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(Brushes.Crimson, new Pen(Brushes.White, 1.2), new Point(8, 8), 7, 7);
            var text = new FormattedText(
                "!",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                10,
                Brushes.White,
                96);
            dc.DrawText(text, new Point(6.5, 1));
        }

        var rtb = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        _helpBadge = rtb;
        return _helpBadge;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    private IntPtr GetHwnd()
    {
        if (_hwnd != IntPtr.Zero) return _hwnd;
        _hwnd = new WindowInteropHelper(this).Handle;
        return _hwnd;
    }

    private void FlashWindow()
    {
        var handle = GetHwnd();
        if (handle == IntPtr.Zero) return;

        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref fi);
    }

    private void StopFlash()
    {
        var handle = GetHwnd();
        if (handle == IntPtr.Zero) return;

        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = FLASHW_STOP,
            uCount = 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref fi);
    }

    /* ================= pembaruan mandiri (self-update) ================= */

    /// <summary>
    /// Memeriksa pembaruan di latar belakang dari GitHub Releases tanpa memblokir antarmuka.
    /// Bila ditemukan rilis yang lebih baru, installer diunduh di latar belakang ke folder staging.
    /// </summary>
    private async Task CheckForAppUpdatesAsync(bool manual = false)
    {
        try
        {
            var update = await UpdateService.CheckGitHubReleaseAsync();
            if (update is null)
            {
                if (manual)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        PushToast($"Aplikasi Retriva sudah menggunakan versi terbaru (v{RetrivaVersion.Current}).", false);
                    });
                }
                return;
            }

            // Unduh file pembaruan di latar belakang ke folder ProgramData/updates (staging)
            _ = Task.Run(async () =>
            {
                _pendingUpdateInstallerPath = await UpdateService.DownloadUpdateInBackgroundAsync(update);
                if (!string.IsNullOrWhiteSpace(_pendingUpdateInstallerPath))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        PushJson(new
                        {
                            type = "updateReady",
                            version = update.Version,
                            installerPath = _pendingUpdateInstallerPath
                        });
                    });
                }
            });

            await Dispatcher.InvokeAsync(() =>
            {
                PushToast($"Pembaruan Retriva v{update.Version} ditemukan dan sedang diunduh di latar belakang...", false);
                PushJson(new
                {
                    type = "updateAvailable",
                    version = update.Version,
                    title = update.Title,
                    notes = update.ReleaseNotes,
                    downloadUrl = update.DownloadUrl,
                    publishedAt = update.PublishedAt
                });
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pengecekan pembaruan GitHub tidak berhasil");
            if (manual)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    PushToast("Gagal memeriksa pembaruan: periksa koneksi internet.", true);
                });
            }
        }
    }

    /* ================= panel lain ================= */

    /// <summary>
    /// Membuka pendaftaran dan menyetujui panel yang menunggu.
    ///
    /// Seluruhnya lewat Superadmin, termasuk saat panel ini sendiri yang
    /// menjadi Superadmin. Ia memeriksa wewenang di setiap metode, jadi panel
    /// Dosen yang mengirim pesan ini akan ditolak dengan alasan yang jelas —
    /// bukan diam-diam gagal karena tombolnya kebetulan tersembunyi.
    /// </summary>
    private void HandlePanels(string type, string json)
    {
        var request = JsonSerializer.Deserialize<PanelRequest>(json, JsonOpts);

        switch (type)
        {
            case "unitAlias":
            {
                if (request?.UnitId is not { Length: > 0 } aliasUnit) return;

                // Alias kosong berarti MENGHAPUS alias, bukan menyimpan string
                // kosong. Alias kosong yang tersimpan akan tampil sebagai kartu
                // tanpa nama sama sekali — lebih buruk daripada unit id apa adanya.
                Run(async () =>
                {
                    await _link.SetAliasAsync(aliasUnit, request.Alias);
                    await SeedFromSuperadminAsync();
                }, "menyimpan nama unit");
                break;
            }

            case "unitRename":
            {
                if (request?.UnitId is not { Length: > 0 } oldName) return;
                if (request.Alias is not { Length: > 0 } newName) return;

                Run(async () =>
                {
                    // Alasan penolakan ditampilkan apa adanya, bukan diringkas
                    // menjadi "gagal". Nama yang bentrok dan unit yang tidak ada
                    // menuntut tindakan yang berbeda dari orang yang membacanya.
                    var reason = await _link.RenameUnitAsync(oldName, newName);
                    await Dispatcher.InvokeAsync(() => PushToast(
                        reason ?? $"{oldName} kini bernama {newName.Trim()}", reason is not null));

                    if (reason is null) await SeedFromSuperadminAsync();
                }, "mengganti nama unit");
                break;
            }

            case "examStart":
            {
                var title = request?.Title;
                Run(async () =>
                {
                    var session = await _link.StartExamAsync(title ?? "");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (session is null)
                        {
                            PushToast("Tidak berwenang memulai sesi ujian", true);
                            return;
                        }

                        // Judul yang dikembalikan bisa BERBEDA dari yang diminta:
                        // koordinator menolak sesi kedua dan mengembalikan sesi
                        // yang sedang berjalan. Membandingkannya di sini membuat
                        // bedanya terlihat, bukan tampak seperti sesi baru.
                        PushToast(string.Equals(session.Title, (title ?? "").Trim(),
                                      StringComparison.Ordinal)
                            ? $"Sesi ujian dimulai: {session.Title}"
                            : $"Sesi \"{session.Title}\" sudah berjalan sejak "
                              + $"{session.StartedAt.ToLocalTime():HH:mm} oleh {session.StartedBy}",
                            !string.Equals(session.Title, (title ?? "").Trim(), StringComparison.Ordinal));
                    });
                    await PushExamAsync();
                }, "memulai sesi ujian");
                break;
            }

            case "examEnd":
            {
                Run(async () =>
                {
                    var ended = await _link.EndExamAsync();
                    await Dispatcher.InvokeAsync(() => PushToast(ended
                        ? "Sesi ujian diakhiri · pemantauan dihentikan di semua unit"
                        : "Tidak ada sesi berjalan · perintah berhenti tetap dikirim", !ended));
                    await PushExamAsync();
                }, "mengakhiri sesi ujian");
                break;
            }

            case "examRefresh":
            {
                Run(PushExamAsync, "memuat keadaan sesi ujian");
                break;
            }

            case "examTimeline":
            {
                if (request?.UnitId is not { Length: > 0 } examUnit) return;

                Run(async () =>
                {
                    if (_link.Exam is not { } session)
                    {
                        await Dispatcher.InvokeAsync(() =>
                            PushJson(new { type = "examTimeline", unitId = examUnit, events = Array.Empty<object>() }));
                        return;
                    }

                    var events = await _link.GetExamTimelineAsync(session.Id, examUnit);
                    await Dispatcher.InvokeAsync(() => PushJson(new
                    {
                        type = "examTimeline",
                        unitId = examUnit,
                        events = events.Select(e => new
                        {
                            kind = e.Kind.ToString(),
                            description = e.Description,
                            detectedAt = e.DetectedAt,
                        }),
                    }));
                }, "memuat lini masa ujian");
                break;
            }

            case "unitFullscreen":
            {
                if (request?.UnitId is not { Length: > 0 } fsUnit) return;
                var enable = request.Fullscreen ?? false;

                Run(async () =>
                {
                    await _link.SetAutoFullscreenAsync(fsUnit, enable);
                    await SeedFromSuperadminAsync();

                    await Dispatcher.InvokeAsync(() => PushToast(enable
                        ? $"{fsUnit} akan membuka siaran layar penuh otomatis"
                        : $"{fsUnit} tidak lagi membuka siaran otomatis"));
                }, "mengubah setelan layar penuh");
                break;
            }

            case "linkJoin":
            {
                var host = request?.Host?.Trim();
                if (string.IsNullOrEmpty(host))
                {
                    PushToast("Alamat PC Asisten belum diisi", true);
                    return;
                }

                var role = Enum.TryParse<PanelRole>(request?.Role, ignoreCase: true, out var wanted)
                    && wanted is PanelRole.Dosen or PanelRole.Asisten
                        ? wanted
                        : PanelRole.Dosen;

                var port = request?.Port is > 0 and < 65536 ? request.Port.Value : _config.ListenPort;

                // Tidak ada yang perlu dilepas lebih dulu. Setiap lab berdiri
                // sendiri sekarang, dan token yang dikirim dipilih berdasarkan
                // sidik jari lab tujuan — bukan token terakhir yang kebetulan
                // tersimpan.
                Run(() => _link.ConnectAsync(host, port, requestedRole: role),
                    "bergabung ke lab");
                break;
            }

            case "linkRetry":
                Run(() => _link.ConnectAsync(_link.CoordinatorHost, _link.CoordinatorPort),
                    "menyambung ulang");
                break;

            case "linkBecomeSuperadmin":
                /*
                    Mengubah jawaban dialog penyiapan.

                    Ada karena jawaban itu tersimpan permanen dan aplikasinya
                    tidak menanyakannya lagi. Mesin yang terlanjur dijawab
                    "bergabung ke lab" akan terbuka sebagai panel Dosen
                    selamanya — dan kalau mesin itu satu-satunya yang ada, tidak
                    ada Superadmin yang berjalan, tidak ada yang bisa membuka
                    pendaftaran, dan panelnya tidak akan pernah tertaut.

                    Jalan buntu yang sempurna, dan satu-satunya jalan keluarnya
                    sebelum ini adalah menghapus panel.json lewat Explorer.
                */
                Run(async () =>
                {
                    await _link.ForgetLabAsync(_link.CoordinatorThumbprint);
                    _link.PromoteToSuperadmin();

                    await Dispatcher.InvokeAsync(() => PushToast(
                        "Mesin ini disetel menjadi Superadmin — tutup lalu buka Retriva "
                      + "untuk menyalakan layanan lab."));
                }, "menyetel mesin ini sebagai Superadmin");
                break;

            case "linkReset":
                Run(async () =>
                {
                    await _link.ForgetLabAsync(request?.Thumbprint);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _linkFailure = null;
                        PushToast("Tautan dilepas — masukkan kode pendaftaran lab yang baru");
                    });
                }, "melepas tautan lab");
                break;

            case "panelsRefresh":
                // Panel yang belum tertaut memang tidak punya daftar untuk
                // dimuat. Meneruskannya akan menghasilkan pemberitahuan galat
                // setiap kali layar ini dibuka — padahal keadaannya normal.
                if (_link.Ready) Run(PushPanelsAsync, "memuat daftar panel");

                // Penemuan lab dinyalakan saat layar penautan dibuka, bukan
                // sejak aplikasi start. Panel yang sudah tertaut tidak pernah
                // membutuhkannya, dan soket multicast yang menganggur sepanjang
                // hari hanya menambah hal yang bisa gagal diam-diam.
                _discovery?.Start();
                PushLabs();
                break;

            case "enrollmentOpen":
                Run(async () =>
                {
                    var window = await _link.OpenEnrollmentAsync();
                    await Dispatcher.InvokeAsync(() => PushEnrollment(window));
                    await PushPanelsAsync();
                }, "membuka pendaftaran");
                break;

            case "enrollmentClose":
                Run(async () =>
                {
                    var window = await _link.CloseEnrollmentAsync();
                    await Dispatcher.InvokeAsync(() => PushEnrollment(window));
                }, "menutup pendaftaran");
                break;

            case "panelApprove":
            {
                if (request?.DeviceId is not { } deviceId) return;
                if (!Enum.TryParse<PanelRole>(request.Role, ignoreCase: true, out var role)) return;

                Run(async () =>
                {
                    var ok = await _link.ApprovePanelAsync(deviceId, role);
                    await Dispatcher.InvokeAsync(() => PushToast(
                        ok ? $"Panel disetujui sebagai {role}" : "Persetujuan ditolak", !ok));
                    await PushPanelsAsync();
                }, "menyetujui panel");
                break;
            }

            case "panelRevoke":
            {
                if (request?.DeviceId is not { } deviceId) return;

                Run(async () =>
                {
                    var ok = await _link.RevokePanelAsync(deviceId);
                    await Dispatcher.InvokeAsync(() => PushToast(
                        ok ? "Wewenang panel dicabut" : "Pencabutan ditolak", !ok));
                    await PushPanelsAsync();
                }, "mencabut wewenang panel");
                break;
            }
        }
    }

    /// <summary>
    /// Daftar panel yang didorong Superadmin tanpa diminta.
    ///
    /// Permintaan bergabung datang kapan saja, termasuk saat layar Panel admin
    /// sedang terbuka. Tanpa jalur ini asisten harus berpindah menu lalu
    /// kembali untuk melihatnya — dan sementara itu dosen menunggu di depan
    /// kelas tanpa tahu permintaannya sudah sampai atau belum.
    /// </summary>
    /// <summary>
    /// Keadaan siaran lab, termasuk siaran milik panel LAIN.
    ///
    /// Panel yang tidak sedang menyiarkan tetap perlu tahu ada siaran berjalan
    /// dan milik siapa. Tanpa itu dua orang menekan "Mulai siaran" berselang
    /// beberapa detik, yang kedua ditolak tanpa mengerti sebabnya — atau lebih
    /// buruk, merebut tanpa sadar bahwa ada kelas lain yang sedang berjalan.
    /// </summary>
    private void OnBroadcastStateChanged(BroadcastState state) =>
        Dispatcher.BeginInvoke(() => PushJson(new
        {
            type = "broadcastState",
            active = state.Active,
            sourceLabel = state.SourceLabel,
            heldBy = state.HeldByPanel,
            receivers = state.Receivers,
            mbps = Math.Round(state.Mbps, 1),
        }));

    private void OnPanelsChanged(PanelSummary[] panels) =>
        Dispatcher.BeginInvoke(() => PushPanels(panels));

    private async Task PushPanelsAsync()
    {
        var panels = await _link.GetPanelsAsync();
        var window = await _link.GetEnrollmentAsync();

        await Dispatcher.InvokeAsync(() =>
        {
            PushPanels(panels);
            PushEnrollment(window);
        });
    }

    private void PushPanels(PanelSummary[] panels)
    {
            PushJson(new
            {
                type = "panels",
                panels = panels.Select(p => new
                {
                    deviceId = p.DeviceId,
                    displayName = p.DisplayName,

                    // Enam karakter pertama, sama persis dengan yang tampil di
                    // layar panel yang meminta. Itu yang dicocokkan asisten.
                    code = p.DeviceId.Length >= 6 ? p.DeviceId[..6].ToUpperInvariant() : p.DeviceId,

                    role = p.Role.ToString(),
                    requestedRole = p.RequestedRole.ToString(),
                    revoked = p.Revoked,
                    online = p.Online,
                    wired = p.Wired,
                    lastSeen = p.LastSeen,
                }),
            });
    }

    private void PushEnrollment(EnrollmentWindowDto window) => PushJson(new
    {
        type = "enrollment",
        open = window.Open,
        expiresAt = window.ExpiresAt,
        thumbprint = window.Thumbprint,
    });

    private sealed record PanelRequest(
        string? DeviceId, string? Role, string? Host, int? Port, string? Thumbprint,
        string? UnitId, string? Alias, bool? Fullscreen,
        /// <summary>Judul sesi ujian yang diketik asisten.</summary>
        string? Title);

    /// <summary>
    /// Mengirim keadaan sesi ujian beserta hitungan peristiwa per unit.
    ///
    /// Hitungan diambil terpisah dari sesinya karena ia berubah terus sementara
    /// sesinya tidak. Digabung, panel harus memuat ulang seluruh keadaan sesi
    /// hanya untuk menyegarkan sebuah angka.
    /// </summary>
    private async Task PushExamAsync()
    {
        var session = await _link.GetExamAsync();

        var summaries = session is null
            ? []
            : await _link.GetExamSummariesAsync(session.Id);

        // Hitungan tetap dikirim dalam bentuk lamanya. Ia dipakai untuk
        // menentukan unit mana yang belum punya catatan sama sekali — dan
        // menurunkannya di sini lebih murah daripada satu pertanyaan lagi.
        var counts = summaries.ToDictionary(p => p.Key, p => p.Value.Count,
            StringComparer.OrdinalIgnoreCase);

        await Dispatcher.InvokeAsync(() => PushJson(new
        {
            type = "exam",
            session = session is null ? null : new
            {
                id = session.Id,
                title = session.Title,
                startedBy = session.StartedBy,
                startedAt = session.StartedAt,
                eventCount = session.EventCount,
            },
            counts,
            summaries = summaries.ToDictionary(p => p.Key, p => new
            {
                count = p.Value.Count,
                lastKind = p.Value.LastKind.ToString(),
                lastDescription = p.Value.LastDescription,
                lastSeenAt = p.Value.LastSeenAt,
            }),
        }));
    }

    /* ================= penemuan lab ================= */

    /// <summary>
    /// Pendengar beacon lab. Null pada panel Superadmin — ia adalah labnya.
    /// </summary>
    private LabDiscovery? _discovery;

    public void AttachDiscovery(LabDiscovery discovery)
    {
        _discovery = discovery;
        discovery.Changed += () => Dispatcher.BeginInvoke(PushLabs);
    }

    private void PushLabs()
    {
        if (_discovery is null) return;

        /*
            Lab yang sudah dikenal disambungi tanpa diminta.

            Inilah yang membuat dosen dapat berpindah ruangan tanpa menyentuh
            apa pun: panelnya mendengar beacon lab tujuan, mengenali sidik
            jarinya di antara lab yang pernah menyetujuinya, dan menyambung
            sendiri. Yang belum dikenal tetap menunggu satu klik dan satu
            persetujuan — sekali seumur hidup lab itu.
        */
        if (!_link.Connected)
        {
            var heard = _discovery.Labs
                .Where(l => l.Thumbprint is not null)
                .Select(l => (Thumbprint: l.Thumbprint!, l.Host, l.Port))
                .ToList();

            if (heard.Count > 0)
                Run(() => _link.TryAutoConnectAsync(heard), "menyambung otomatis ke lab");
        }

        PushJson(new
        {
            type = "labs",
            labs = _discovery.Labs.Select(l => new
            {
                labName = l.LabName,
                host = l.Host,
                port = l.Port,
                accepting = l.AcceptingPanels,
                thumbprint = l.Thumbprint,
            }),
        });
    }

    /// <summary>Memulai atau mengakhiri sesi kendali jarak jauh atas satu unit.</summary>
    private void HandleRemote(string type, string json)
    {
        var request = JsonSerializer.Deserialize<RemoteRequest>(json, JsonOpts);

        if (type == "remoteEnd")
        {
            EndRemote("diakhiri admin");
            return;
        }

        var unitId = request?.UnitId;
        if (unitId is null)
        {
            PushToast("Unit tujuan belum dipilih", true);
            return;
        }

        var allowInput = request?.AllowInput ?? false;
        var fps = Math.Clamp(request?.Fps ?? 15, 5, 30);

        // Penerima dinyalakan SEBELUM perintah dikirim.
        //
        // Klien mulai mengirim segera setelah menerima perintah. Kalau soket
        // admin belum mendengarkan, frame-frame pertama ditolak kernel dan
        // gejalanya adalah beberapa detik layar kosong di awal setiap sesi —
        // yang mudah disalahartikan sebagai klien yang lambat merespons.
        _remote.Start(unitId, allowInput);
        _remoteTimer.Start();

        var command = new BeginRemoteCommand
        {
            FrameRate = fps,
            AllowInput = allowInput,
        };

        Dispatch(command, allowInput ? "remote.control" : "remote.view", [unitId],
                 $"Kendali {unitId} dimulai");

        _log.LogInformation("Kendali {Mode} atas {Unit} · {Fps} fps · menunggu frame di port {Port}",
            allowInput ? "penuh" : "lihat-saja", unitId, fps, FramePacket.RemoteControlPort);

        PushJson(new { type = "remoteStarted", unitId, allowInput });
    }

    /// <summary>
    /// Meneruskan satu peristiwa mouse atau keyboard ke unit yang dikendalikan.
    ///
    /// Tidak dicatat ke log aktivitas dan tidak dilacak sebagai batch. Gerakan
    /// mouse datang puluhan kali per detik; mencatat tiap satu akan memenuhi
    /// basis data dengan derau dalam hitungan menit dan mengubur peristiwa yang
    /// benar-benar perlu diaudit.
    /// </summary>
    private void HandleRemoteInput(string json)
    {
        if (!_remote.Active || !_remote.InputAllowed) return;
        if (_remote.UnitId is not { } unitId) return;

        var request = JsonSerializer.Deserialize<RemoteInputRequest>(json, JsonOpts);
        if (request?.Kind is not { } kindName) return;
        if (!Enum.TryParse<InputEventKind>(kindName, ignoreCase: true, out var kind)) return;

        var command = new InjectInputCommand
        {
            Kind = kind,
            X = Math.Clamp(request.X ?? 0, 0, 10000),
            Y = Math.Clamp(request.Y ?? 0, 0, 10000),
            KeyCode = request.KeyCode ?? 0,
            WheelDelta = request.WheelDelta ?? 0,
            Button = request.Button ?? 0,
        };

        // Lewat jalur RelayInput, bukan Dispatch: tanpa audit dan tanpa batch.
        // Dan tanpa Run — kegagalan satu gerakan mouse tidak layak memunculkan
        // pemberitahuan, karena gerakan berikutnya datang dalam 30 milidetik
        // dan pemberitahuannya akan datang puluhan kali per detik.
        _ = _link.RelayInputAsync(unitId, command);
    }

    /// <summary>Mengirim frame terakhir dari unit yang dikendalikan ke UI.</summary>
    private void PushRemoteFrame()
    {
        if (!_pageLoaded || _web.CoreWebView2 is null || !_remote.Active) return;

        // Aliran yang mati tidak mengirim kabar apa pun, termasuk kabar bahwa ia
        // mati. Tanpa batas waktu ini, frame terakhir membeku di panel dan
        // tampak seperti mahasiswa yang sedang tidak menyentuh apa-apa.
        if (_remote.FramesReceived > 0 && _remote.SinceLastFrame > TimeSpan.FromSeconds(15))
        {
            EndRemote("aliran layar berhenti selama 15 detik");
            return;
        }

        var frame = _remote.LastFrame;

        PushJson(new
        {
            type = "remoteFrame",
            unitId = _remote.UnitId,
            dataUrl = frame.Length == 0
                ? null
                : "data:image/jpeg;base64," + Convert.ToBase64String(frame),
            width = _remote.FrameWidth,
            height = _remote.FrameHeight,
            frames = _remote.FramesReceived,
            waiting = _remote.FramesReceived == 0,
        });
    }

    private void EndRemote(string reason)
    {
        if (!_remote.Active) return;

        var unitId = _remote.UnitId;
        _remoteTimer.Stop();
        _remote.Stop();

        if (unitId is not null)
        {
            // Toast sukses ditekan: yang perlu dibaca orang adalah alasan
            // berakhirnya, yang sudah ditampilkan di bawah ini. Dua
            // pemberitahuan berurutan untuk satu kejadian membuat yang kedua
            // menimpa yang pertama sebelum sempat terbaca.
            Dispatch(new EndRemoteCommand(), "remote.end", [unitId], successToast: "");
        }

        _log.LogInformation("Kendali atas {Unit} berakhir: {Reason}", unitId, reason);
        PushJson(new { type = "remoteEnded", unitId, reason });
        PushToast($"Kendali {unitId} berakhir: {reason}");
    }

    /// <summary>
    /// Menghentikan siaran, entah diminta pengguna atau karena gugur sendiri.
    ///
    /// Satu jalur untuk kedua sebab, supaya penghentian otomatis tidak pernah
    /// melewatkan langkah yang dilakukan penghentian manual — misalnya lupa
    /// memberi tahu klien agar menutup jendela penonton, yang akan meninggalkan
    /// 40 jendela hitam terbuka di lab.
    /// </summary>
    /// <summary>
    /// Unit yang sedang menyiarkan layarnya, null bila panel ini yang menyiarkan.
    ///
    /// Perlu diingat supaya penghentiannya dapat dialamatkan. Tanpa ini,
    /// menghentikan presentasi mahasiswa hanya menutup penonton di unit lain
    /// sementara unit sumber terus mengalirkan layarnya ke relay — dan
    /// mahasiswa itu tetap melihat papan "layar Anda sedang ditampilkan".
    /// </summary>
    private string? _presentingUnit;

    /// <summary>Penerima multicast yang memberi isi kotak pratinjau saat mahasiswa presentasi.</summary>
    private PresentWatcher? _presentWatch;

    private void EndBroadcast(string? reason = null)
    {
        _previewTimer.Stop();
        PushBroadcastPreview(clear: true);
        _ = _broadcast?.StopAsync();

        _presentWatch?.Dispose();
        _presentWatch = null;

        // Unit sumber dihentikan lebih dulu, sebelum penontonnya ditutup.
        //
        // Urutan terbalik meninggalkan mahasiswa yang terus mengalirkan layarnya
        // ke relay tanpa satu pun penonton — dan papan "layar Anda sedang
        // ditampilkan ke kelas" tetap terpampang di layarnya.
        if (_presentingUnit is { } presenter)
        {
            _presentingUnit = null;
            Run(() => _link.DispatchAsync(
                    new StopPresentingCommand(), "present.stop", [presenter]),
                "menghentikan presentasi mahasiswa");
        }

        // Hak siaran dilepas di Superadmin, bukan hanya penangkapan dihentikan
        // di sini. Tanpa ini panel lain tetap ditolak karena siaran dianggap
        // masih dipegang — oleh panel yang sudah berhenti menyiarkan.
        Run(() => _link.ReleaseBroadcastAsync(), "melepas hak siaran");

        Dispatch(new StopBroadcastCommand(), "broadcast.stop", successToast: "");

        // UI menyimpan keadaan siarannya sendiri, jadi harus diberi tahu.
        // Tanpa ini tombol tetap berbunyi "Hentikan siaran" padahal sudah
        // berhenti — dan pengguna menekannya lagi tanpa hasil.
        PushJson(new { type = "broadcastEnded", reason });

        if (reason is null) _log.LogInformation("Siaran dihentikan");
        else
        {
            _log.LogWarning("Siaran berhenti sendiri: {Reason}", reason);
            PushToast($"Siaran berhenti: {reason}", true);
        }
    }

    /// <summary>Mengirim satu objek apa adanya ke UI.</summary>
    private void PushJson(object payload)
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private void PushToast(string text, bool warning = false)
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;
        var payload = new { type = "toast", text, warning };
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>Menyimpan perubahan pengaturan dari UI ke berkas JSON.</summary>
    private void SaveSettings(string json)
    {
        try
        {
            var incoming = JsonSerializer.Deserialize<SettingsPatch>(json, JsonOpts);
            if (incoming is null) return;

            _config.RequireStudentLogin = incoming.RequireStudentLogin ?? _config.RequireStudentLogin;
            _config.AllowGuestAccess = incoming.AllowGuestAccess ?? _config.AllowGuestAccess;
            _config.StudentsMayRejectControl =
                incoming.StudentsMayRejectControl ?? _config.StudentsMayRejectControl;

            if (!string.IsNullOrWhiteSpace(incoming.LabName))
                _config.LabName = incoming.LabName.Trim();

            /*
                Awalan subnet dinormalkan, tidak diterima apa adanya.

                Nilainya dipakai dengan StartsWith untuk memutuskan mesin mana
                yang berhak dinomori sebagai PC mahasiswa. "192.168.1" tanpa
                titik akhir juga cocok dengan 192.168.10.x dan 192.168.19.x —
                seluruh subnet tetangga ikut dinomori, dan nomornya bentrok.

                Kesalahan mengetiknya tidak menghasilkan pesan apa pun; yang
                terlihat hanyalah unit yang muncul dengan nama yang salah,
                beberapa hari kemudian, tanpa hubungan yang jelas dengan
                sebuah titik yang lupa diketik.
            */
            if (!string.IsNullOrWhiteSpace(incoming.SubnetPrefix))
            {
                var prefix = incoming.SubnetPrefix.Trim();
                _config.SubnetPrefix = prefix.EndsWith('.') ? prefix : prefix + ".";
            }

            /*
                Tabel penomoran diganti UTUH, bukan digabung per baris.

                Penggabungan tidak punya cara menyatakan "baris ini dihapus":
                oktet yang dibuang di UI akan tetap ada di berkas dan tetap
                menamai unit. Asisten yang menghapus satu baris lalu melihatnya
                muncul kembali setelah aplikasi dibuka ulang tidak akan
                menyimpulkan itu bug penggabungan — ia akan menyimpulkan
                aplikasinya tidak menyimpan apa pun.

                Null tetap berarti "tidak diubah". Yang menyimpan satu saklar
                tidak ikut mengosongkan seluruh tabel.
            */
            if (incoming.UnitNames is { } table)
            {
                var next = new Dictionary<int, string>();

                foreach (var (key, name) in table)
                {
                    if (!int.TryParse(key, out var octet)) continue;
                    if (octet is <= 0 or > 254) continue;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    next[octet] = name.Trim();
                }

                _config.UnitNames = next;
            }

            /*
                Port disimpan, tetapi TIDAK berlaku sampai koordinator dimulai
                ulang — Kestrel sudah mengikat porta lamanya saat proses ini
                dijalankan, dan tidak ada cara memindahkannya tanpa restart.

                Penting bahwa UI mengatakan itu. Asisten yang mengubah port lalu
                melihat "Tersimpan" akan menyimpulkan lab sudah berpindah porta,
                padahal empat puluh agent masih menyambung ke yang lama. Yang
                gagal nanti bukan penyimpanannya, melainkan pemahamannya.
            */
            var portChanged = incoming.ListenPort is > 0 and < 65536
                              && incoming.ListenPort.Value != _config.ListenPort;
            if (portChanged) _config.ListenPort = incoming.ListenPort!.Value;

            ConfigStore.Save(RetrivaPaths.ConsoleConfigFile, _config);
            Run(() => _link.WriteActivityAsync("settings.saved"), "mencatat perubahan pengaturan");
            PushSettings();

            /*
                Aturan akses didorong ke seluruh agent, tidak hanya disimpan.

                Menyimpan saja berarti saklarnya baru berlaku bagi unit yang
                mendaftar SETELAHNYA — yaitu setelah lab dinyalakan ulang.
                Asisten yang mematikan kewajiban login lalu melihat mahasiswa
                tetap dimintai NIM akan menyimpulkan saklarnya rusak, dan ia
                tidak akan salah dengan cara yang berguna.
            */
            Run(() => _link.PublishAccessPolicyAsync(), "menyebarkan setelan akses");

            if (portChanged)
            {
                PushToast(
                    $"Port disimpan sebagai {_config.ListenPort}, berlaku setelah koordinator dimulai ulang",
                    true);
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[settings] gagal disimpan: {ex.Message}");
        }
    }

    /// <summary>Mengirim konfigurasi yang perlu ditampilkan UI, termasuk nilai
    /// yang harus disalin ke installer agent.</summary>
    private void PushSettings()
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;

        var payload = new
        {
            type = "settings",
            settings = new
            {
                role = _config.Role,
                labName = _config.LabName,
                // Angka hitung mundur dikirim ke UI supaya dialog konfirmasi
                // menyebut nilai yang benar-benar dipakai perintahnya.
                shutdownDelay = PowerDelays.Shutdown,
                restartDelay = PowerDelays.Restart,

                subnetPrefix = _config.SubnetPrefix,
                // Kunci dijadikan string karena JSON tidak punya kunci
                // objek bertipe angka. Sisi UI mengembalikannya apa adanya.
                unitNames = _config.UnitNames.ToDictionary(
                    kv => kv.Key.ToString(), kv => kv.Value),

                /*
                    Layar Konfigurasi hanya dapat disunting DI MESIN
                    KOORDINATOR, bukan di setiap panel yang punya hak Configure.

                    console.json bersifat lokal. Asisten yang menyunting tabel
                    penomoran dari laptopnya sendiri akan menyimpannya ke berkas
                    yang tidak dibaca koordinator mana pun — tersimpan dengan
                    benar, muncul kembali dengan benar setelah dibuka ulang, dan
                    tidak berpengaruh pada satu unit pun.
                */
                configEditable = _link.IsSuperadminMachine,
                listenPort = _config.ListenPort,
                dataDirectory = RetrivaPaths.DataDir,
                agentEnrollmentSecret = _config.AgentEnrollmentSecret,
                certificateThumbprint = _thumbprint,
                requireStudentLogin = _config.RequireStudentLogin,
                allowGuestAccess = _config.AllowGuestAccess,
                studentsMayRejectControl = _config.StudentsMayRejectControl,
                appVersion = RetrivaVersion.Current,
            },
        };

        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private sealed record UiMessage
    {
        public string Type { get; init; } = string.Empty;
    }

    private sealed record UnitTarget
    {
        public string? UnitId { get; init; }
    }

    private sealed record FileRequest
    {
        /// <summary>Path berkas di komputer admin, hasil dari dialog pemilih.</summary>
        public string[]? Paths { get; init; }
        /// <summary>Unit tujuan; kosong berarti seluruh unit yang tersambung.</summary>
        public string[]? Targets { get; init; }
    }

    private sealed record ChatRequest
    {
        public string? UnitId { get; init; }
        public string? Text { get; init; }
        public bool? ToEveryone { get; init; }
    }

    private sealed record RemoteRequest
    {
        public string? UnitId { get; init; }
        public bool? AllowInput { get; init; }
        public int? Fps { get; init; }
    }

    /// <summary>
    /// Koordinat datang sebagai pecahan per-sepuluh-ribu, bukan piksel.
    ///
    /// Panel admin melihat JPEG yang sudah diperkecil dan tidak tahu resolusi
    /// fisik layar mahasiswa. Mengirim piksel berarti menebak, dan tebakan itu
    /// salah pada layar dengan penskalaan DPI berbeda.
    /// </summary>
    private sealed record RemoteInputRequest
    {
        public string? Kind { get; init; }
        public int? X { get; init; }
        public int? Y { get; init; }
        public int? KeyCode { get; init; }
        public int? WheelDelta { get; init; }
        public int? Button { get; init; }
    }

    private sealed record BroadcastRequest
    {
        public int? Resolution { get; init; }
        public int? Fps { get; init; }
        public bool? Frozen { get; init; }
        public int? SourceIndex { get; init; }

        /// <summary>Kualitas JPEG 40..98. Lihat catatan di penanganan broadcastStart.</summary>
        public int? Quality { get; init; }

        /// <summary>
        /// Unit yang diminta menyiarkan layarnya, untuk presentasi mahasiswa.
        ///
        /// Hanya dipakai broadcastUnit. Pada siaran biasa, yang menangkap layar
        /// adalah panel ini sendiri dan tidak ada unit sumber.
        /// </summary>
        public string? UnitId { get; init; }

        /// <summary>
        /// Merebut siaran yang sedang dipegang panel lain.
        ///
        /// Harus disengaja, tidak pernah bawaan. Asisten kadang memang perlu
        /// memotong siaran dosen yang lupa menghentikannya — tetapi perebutan
        /// yang terjadi diam-diam berarti dosen kehilangan kelasnya di tengah
        /// penjelasan, tanpa tahu mengapa.
        /// </summary>
        public bool? Takeover { get; init; }

        /// <summary>
        /// Unit yang dipilih menerima siaran. Null berarti semua unit online.
        ///
        /// Aliran multicast tetap satu dan sama; yang dibedakan adalah kepada
        /// siapa perintah membuka penonton dikirim. Jadi memilih sebagian kelas
        /// tidak menghemat bandwidth, tetapi memang membatasi siapa yang melihat.
        /// </summary>
        public string[]? Targets { get; init; }
    }

    private sealed record SettingsPatch
    {
        public bool? RequireStudentLogin { get; init; }
        public bool? AllowGuestAccess { get; init; }
        public bool? StudentsMayRejectControl { get; init; }

        // Null berarti "tidak diubah", bukan "dikosongkan". UI mengirim hanya
        // medan yang benar-benar disunting, sehingga menyimpan satu saklar
        // tidak ikut menimpa awalan subnet dengan string kosong.
        public string? LabName { get; init; }
        public string? SubnetPrefix { get; init; }
        public Dictionary<string, string>? UnitNames { get; init; }
        public int? ListenPort { get; init; }
    }
}
