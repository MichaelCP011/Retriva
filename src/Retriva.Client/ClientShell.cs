using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Retriva.Media;
using Retriva.Shared.Local;
using Retriva.Ui;

namespace Retriva.Client;

/// <summary>
/// Jendela mahasiswa: antarmuka React di WebView2, dengan lapisan siaran WPF
/// asli di atasnya.
///
/// Pembagian ini bukan kompromi melainkan keharusan. Antarmukanya dibuat di
/// React supaya mewarisi seluruh sistem desain panel admin — satu berkas CSS
/// untuk keduanya, sehingga tidak bercabang pelan-pelan menjadi dua aplikasi
/// yang kebetulan bernama sama.
///
/// Tetapi siarannya TIDAK boleh lewat WebView2. Frame datang 15 kali per detik;
/// mengirimkannya sebagai base64 melewati jembatan berarti sekitar 5 MB per
/// detik hanya untuk menggambar ulang satu gambar. Karena itu ia dirender
/// sebagai Image WPF biasa di lapisan atas jendela yang sama — sehingga siaran
/// tetap MENYATU di dalam jendela klien, bukan jendela terpisah seperti dulu.
/// </summary>
public sealed class ClientShell : ChromeWindow
{
    private const string VirtualHost = "retriva.client";

    /// <summary>
    /// Host TERPISAH dari host virtual halaman.
    ///
    /// SetVirtualHostNameToFolderMapping memetakan retriva.client ke sebuah
    /// folder, dan permintaan ke host itu dilayani dari disk sebelum sempat
    /// dicegat. Akibatnya /live/frame.jpg dicari sebagai berkas, tidak ketemu,
    /// lalu gambar di halaman tampil sebagai ikon rusak — persis gejala yang
    /// muncul. Host ini tidak dipetakan ke folder mana pun, sehingga
    /// pencegatan sumber daya selalu yang menjawab lebih dulu.
    /// </summary>
    private const string FrameHost = "retriva.live";

    /// <summary>
    /// Alamat frame siaran terkini. Bukan berkas di disk melainkan permintaan
    /// yang dicegat lalu dijawab dari memori — dan justru itu yang membuat
    /// siaran dapat menjadi elemen web biasa: gambar yang bisa ditaruh di mana
    /// saja, diberi ukuran berapa saja, dan dibesarkan ke layar penuh.
    /// </summary>
    public const string FrameUrl = "https://" + FrameHost + "/frame.jpg";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebView2 _web = new();

    private FrameReceiver? _receiver;
    private bool _pageLoaded;
    private long _frames;

    /// <summary>
    /// Frame JPEG terakhir. Diganti utuh, bukan disunting, sehingga pembaca di
    /// thread lain selalu mendapat gambar yang lengkap — tidak ada keadaan
    /// setengah tertulis yang bisa terbaca.
    /// </summary>
    private volatile byte[] _latestFrame = [];

    /// <summary>Pencatat angka penerima selama siaran, dua detik sekali.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _statsTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    /// <summary>Jumlah permintaan frame dari halaman yang dijawab 404.</summary>
    private long _frameMisses;

    /// <summary>
    /// Dipasang Program.cs untuk melaporkan penerimaan siaran ke agent.
    ///
    /// Klien adalah SATU-SATUNYA pihak yang mengetahui apakah frame benar-benar
    /// tiba: multicast UDP tidak punya jalur balik, jadi koordinator tidak akan
    /// pernah tahu siapa yang mendengarkan. Laporan ini yang membuat pertanyaan
    /// "berapa yang menerima siaran saya" punya jawaban jujur.
    /// </summary>
    public Func<long, long, Task>? ReportBroadcast;

    /* ---------------- yang disediakan pemanggil ---------------- */

    /// <summary>nim, nama, tamu. NIM kosong hanya sah bila tamu.</summary>
    public Func<string, string, bool, bool>? SignInRequested;
    public Action? SignOutRequested;
    public Action<string>? ChatSendRequested;
    public Action<bool>? HelpToggleRequested;

    /// <summary>
    /// Nama yang diketik mahasiswa untuk unitnya. String kosong berarti dihapus.
    /// </summary>
    public Action<string>? UnitNameRequested;
    public Action<string>? Diagnostic;
    public event Action? Ready;

    public ClientShell() : base("Retriva", "Mahasiswa", "client")
    {
        Width = 1040;
        Height = 700;
        MinWidth = 720;
        MinHeight = 520;

        SetBody(_web);
        AssetDiagnostic += m => Diagnostic?.Invoke(m);

        _statsTimer.Tick += (_, _) =>
        {
            if (_receiver is null) return;

            var st = _receiver.Stats;

            // Dilaporkan ke agent pada laju yang sama dengan pencatatan log:
            // dua detik sekali. Empat puluh unit yang melapor tiap detik hanya
            // menambah lalu lintas kendali tanpa menambah ketepatan yang berarti.
            if (ReportBroadcast is { } report)
                _ = report(st.FramesComplete, st.PacketsReceived);

            Diagnostic?.Invoke(
                $"[siaran] {st.PacketsReceived} paket · {st.FramesComplete} frame utuh · "
              + $"{st.FramesDropped} gugur · {st.PacketsIgnored} diabaikan · "
              + $"{_frameMisses} permintaan halaman dijawab 404 · "
              + $"frame terakhir {_latestFrame.Length} byte");
        };

        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closed += (_, _) => StopBroadcast();
    }

    /// <summary>
    /// Jendela klien dikecualikan dari tangkapan layar, seperti panel admin.
    ///
    /// HASILNYA DICATAT, tidak dibuang seperti sebelumnya. Pengecualian yang
    /// gagal menghasilkan efek cermin: layar dosen memuat jendela klien yang
    /// menampilkan layar dosen, tiap generasi menambah detail, dan ukuran frame
    /// JPEG memanjat dari 180 KB ke 450 KB tanpa sebab yang terlihat.
    ///
    /// Gejalanya bukan gambar yang aneh — gambarnya tampak biasa — melainkan
    /// bitrate yang naik pelan sampai seluruh kelas tersendat. Tanpa baris ini,
    /// tidak ada cara membedakannya dari layar yang memang sedang ramai.
    /// </summary>
    protected override void OnWindowHandleReady(IntPtr handle) =>
        Diagnostic?.Invoke($"[capture] {CaptureExclusion.Apply(handle)}");

    /* ---------------- WebView2 ---------------- */

    private async Task InitializeWebViewAsync()
    {
        // Folder data ditentukan, bukan dibiarkan mendarat di sebelah exe.
        // Klien dipasang di Program Files, yang tidak dapat ditulis mahasiswa —
        // lihat Retriva.Ui/WebViewData.cs.
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: WebViewData.FolderFor("client"));

        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;

        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) => _pageLoaded = true;

        /*
            Frame siaran disajikan sebagai sumber daya HTTP, bukan dikirim
            sebagai base64 lewat jembatan pesan.

            Bedanya besar. Base64 menggelembungkan tiap frame sepertiga, lalu
            membungkusnya dalam JSON, lalu menyalinnya melintasi jembatan —
            tiga salinan per frame, lima belas kali per detik, seluruhnya
            membebani thread UI. Lewat jalur sumber daya, byte JPEG-nya
            diserahkan apa adanya ke mesin render.

            Dan yang lebih penting: karena ia menjadi gambar biasa di halaman,
            siarannya bisa ditaruh di mana saja, diberi ukuran berapa saja, dan
            dibesarkan ke layar penuh — semuanya dengan CSS. Menumpuk elemen WPF
            di atas WebView2 tidak pernah bisa melakukan itu, karena WebView2
            di-host lewat HWND sendiri dan selalu menggambar paling atas.
        */
        core.AddWebResourceRequestedFilter(
            $"https://{FrameHost}/*", CoreWebView2WebResourceContext.All);

        var loggedFirstRequest = false;

        core.WebResourceRequested += (_, args) =>
        {
            if (!loggedFirstRequest)
            {
                loggedFirstRequest = true;
                Diagnostic?.Invoke($"[siaran] permintaan frame pertama dari halaman: {args.Request.Uri}");
            }

            var bytes = _latestFrame;
            if (bytes.Length == 0)
            {
                // Dihitung, bukan hanya dijawab. Halaman meminta lima belas kali
                // per detik; jumlah penolakan inilah yang membedakan "belum ada
                // frame sama sekali" dari "frame ada tetapi gambarnya rusak".
                System.Threading.Interlocked.Increment(ref _frameMisses);

                args.Response = core.Environment.CreateWebResourceResponse(
                    null, 404, "No Frame", "Cache-Control: no-store");
                return;
            }

            args.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes), 200, "OK",
                "Content-Type: image/jpeg\r\nCache-Control: no-store, max-age=0");
        };

        var devUrl = Environment.GetEnvironmentVariable("RETRIVA_UI_URL");
        if (!string.IsNullOrWhiteSpace(devUrl))
        {
            core.Navigate($"{devUrl.TrimEnd('/')}/client.html");
            return;
        }

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(wwwroot, "client.html")))
        {
            core.NavigateToString(
                "<body style='font-family:Segoe UI;padding:40px'>"
              + "<h2>UI belum ada di folder output</h2>"
              + "<p>Dicari di:<br><code>" + Path.Combine(wwwroot, "client.html") + "</code></p>"
              + "<p>Bila <code>npm run build</code> sudah dijalankan, periksa apakah "
              + "<code>outDir</code> di <code>ui/vite.config.js</code> menunjuk folder yang "
              + "sama dengan <code>Content Include</code> di csproj.</p></body>");
            return;
        }

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate($"https://{VirtualHost}/client.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // React mengirim lewat postMessage(string), jadi WebMessageAsJson
        // mengembalikan string ber-quote. Kedua bentuk diterima supaya
        // jembatannya tidak rapuh terhadap perubahan di sisi web.
        string json;
        try
        {
            var raw = e.WebMessageAsJson;
            json = raw.StartsWith('"')
                ? JsonSerializer.Deserialize<string>(raw) ?? ""
                : raw;
        }
        catch (JsonException) { return; }

        if (string.IsNullOrWhiteSpace(json)) return;

        UiMessage? msg;
        try { msg = JsonSerializer.Deserialize<UiMessage>(json, JsonOpts); }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[klien] pesan tidak dikenali: {ex.Message}");
            return;
        }
        if (msg?.Type is null) return;

        switch (msg.Type)
        {
            case "ready":
                _pageLoaded = true;
                Ready?.Invoke();
                break;

            case "signIn":
            {
                var guest = msg.Guest ?? false;
                var who = msg.DisplayName;
                if (who is not { Length: > 0 }) break;

                // Tamu masuk tanpa NIM; yang bukan tamu tidak boleh. Tanpa
                // syarat ini, halaman yang berubah dapat mengirim NIM kosong
                // dan mahasiswa tercatat sebagai orang tanpa identitas tanpa
                // pernah ditandai tamu.
                var id = msg.Nim ?? "";
                if (!guest && id.Length == 0) break;

                SignInRequested?.Invoke(id, who, guest);
                break;
            }

            case "signOut":
                SignOutRequested?.Invoke();
                break;

            case "remoteConsent":
                // TrySetResult, bukan SetResult. Jawaban yang datang setelah
                // waktunya habis bukan keadaan mustahil — mahasiswa menekan
                // tombol tepat saat penghitungnya nol — dan melemparkan
                // pengecualian di sini akan mematikan penanganan pesan klien
                // seluruhnya.
                _consent?.TrySetResult(msg.Granted ?? false);
                _consent = null;
                break;

            case "chatSend":
                if (msg.Text is { Length: > 0 } text) ChatSendRequested?.Invoke(text);
                break;

            case "presentation":
                /*
                    Halaman yang memutuskan, jendela yang menuruti.

                    Mode layar penuh siaran dimiliki halaman — mahasiswa dapat
                    membukanya dengan klik ganda dan menutupnya dengan Esc, dan
                    PC proyektor membukanya sendiri. Jendela tidak dapat
                    mengetahui semua jalan itu, jadi ia tidak menebak: halaman
                    mengabarkan keadaannya, jendela menyesuaikan bingkainya.

                    Tanpa kabar ini, gambar siaran tetap terpotong bilah judul
                    di atas dan taskbar di bawah — dan presentasi dosen terlihat
                    seperti tangkapan layar, bukan seperti presentasi.
                */
                // onTop hanya benar untuk PC proyektor. Lihat catatan panjang
                // di ChromeWindow.SetPresentationMode.
                SetPresentationMode(msg.On ?? false, msg.OnTop ?? false);
                break;

            case "helpToggle":
                HelpToggleRequested?.Invoke(msg.Raised ?? false);
                break;

            case "setUnitName":
                UnitNameRequested?.Invoke(msg.DisplayName ?? "");
                break;

            case "openFile":
            case "openFolder":
                OpenInExplorer(msg.Type == "openFolder" ? FileDownloaderInbox : msg.Path);
                break;

            case "openLink":
                OpenLink(msg.Url);
                break;

            default:
                Debug.WriteLine($"[klien] jenis pesan belum ditangani: {msg.Type}");
                break;
        }
    }

    /// <summary>
    /// Folder tujuan berkas, DIKATAKAN AGENT di pesan sambutan.
    ///
    /// Sebelumnya dihitung ulang di sini dengan rumus kembar milik agent, dengan
    /// alasan bahwa klien tidak boleh mereferensikan project agent. Batas proses
    /// itu tetap benar — tetapi cara menjaganya bukan menyalin rumusnya,
    /// melainkan menanyakannya lewat protokol yang memang sudah menghubungkan
    /// keduanya.
    ///
    /// Sejak folder tujuan dapat dipilih saat pemasangan, rumus kembar itu
    /// menjadi salah pada setiap mesin yang memilih folder lain: unduhan sukses,
    /// daftar berkas mahasiswa kosong, dan "Buka folder" membuka folder kosong
    /// yang bukan tempat berkasnya berada.
    ///
    /// Nilai bawaan dipertahankan untuk saat klien berjalan sebelum agent
    /// sempat menyapa.
    /// </summary>
    private string FileDownloaderInbox =>
        string.IsNullOrWhiteSpace(_fileInbox)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Retriva")
            : _fileInbox;

    private string _fileInbox = "";

    /// <summary>Disetel dari AgentWelcome, lalu dipakai daftar berkas dan tombol buka folder.</summary>
    public void SetFileInbox(string path) => Dispatcher.Invoke(() =>
    {
        if (string.Equals(_fileInbox, path, StringComparison.OrdinalIgnoreCase)) return;

        _fileInbox = path ?? "";
        Diagnostic?.Invoke($"[berkas] folder tujuan: {FileDownloaderInbox}");

        // Daftar disusun ulang segera. Tanpa ini, folder yang baru diketahui
        // baru terpakai pada penyegaran berikutnya — dan penyegaran itu dipicu
        // oleh kedatangan berkas, yaitu hal yang sedang ditunggu.
        PushInbox();
    });

    /// <summary>
    /// Membuka tautan dari pesan asisten di peramban bawaan.
    ///
    /// DIPERIKSA DI SINI, BUKAN DI HALAMAN.
    ///
    /// Halaman sudah menyaring apa yang ditampilkannya sebagai tautan, tetapi
    /// penyaringan itu tidak dapat dijadikan jaminan: halaman berjalan di
    /// WebView2 dan menerima teks dari jaringan. Yang menentukan apa yang
    /// benar-benar dijalankan adalah baris ini, jadi pemeriksaannya harus di
    /// sini juga — bukan sebagai kerangkapan, melainkan karena hanya sisi ini
    /// yang tidak dapat dilewati.
    ///
    /// Hanya http dan https. Skema lain bukan sekadar tidak berguna di ruang
    /// praktikum, sebagian di antaranya berbahaya: file: membuka isi disk,
    /// dan skema penangan aplikasi seperti ms-msdt: pernah menjadi jalan masuk
    /// eksekusi kode lewat tautan yang tampak biasa.
    ///
    /// UseShellExecute true, dan URL diserahkan sebagai satu argumen tunggal
    /// lewat ProcessStartInfo. Menyusunnya menjadi baris perintah berisi teks
    /// dari jaringan adalah cara paling langsung mengubah sebuah tautan menjadi
    /// perintah.
    /// </summary>
    private void OpenLink(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            Diagnostic?.Invoke($"[tautan] bukan alamat yang sah: {Trim(raw)}");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            Diagnostic?.Invoke($"[tautan] skema {uri.Scheme} ditolak: {Trim(raw)}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            Diagnostic?.Invoke($"[tautan] dibuka: {Trim(uri.AbsoluteUri)}");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"[tautan] gagal dibuka: {ex.Message}");
        }
    }

    /// <summary>Memotong alamat panjang agar satu baris log tetap satu baris.</summary>
    private static string Trim(string value) =>
        value.Length <= 120 ? value : value[..120] + "…";

    /// <summary>
    /// Membuka berkas atau folder lewat Explorer.
    ///
    /// UseShellExecute wajib true. Tanpanya, Process.Start mencoba menjalankan
    /// berkasnya sebagai program — dan sebuah PDF akan menghasilkan pengecualian
    /// alih-alih terbuka di pembaca PDF.
    /// </summary>
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                // Dipilih di Explorer, bukan langsung dibuka. Berkas yang datang
                // dari jaringan sebaiknya dilihat mahasiswa lebih dulu sebelum
                // dijalankan, terutama bila kebetulan berekstensi executable.
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                Diagnostic?.Invoke($"[berkas] tidak ditemukan: {path}");
            }
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"[berkas] gagal membuka: {ex.Message}");
        }
    }

    /* ---------------- dorongan ke UI ---------------- */

    private void Push(object payload)
    {
        if (!_pageLoaded || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    public void PushState(bool connected, string? unitId, bool requireLogin,
                          bool allowGuest, string? windowsUser, string? teacher = null) =>
        Dispatcher.Invoke(() => Push(new
        {
            type = "state", connected, unitId, requireLogin, allowGuest, windowsUser, teacher,
            selfName = ReadSelfName(),
        }));

    /// <summary>
    /// Nama unit yang tersimpan di mesin ini, atau null bila belum pernah diisi.
    ///
    /// Dibaca dari berkas, bukan disimpan sebagai medan. Nama ini ditulis oleh
    /// jalur lain (penangan UnitNameRequested di Program), dan medan yang
    /// disalin sekali saat start akan menampilkan nama lama pada setiap
    /// pembaruan keadaan sesudahnya — kotak isian yang berbalik sendiri ke nilai
    /// sebelumnya beberapa detik setelah diisi.
    /// </summary>
    public static string? ReadSelfName()
    {
        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Retriva", "unit-name.txt");

            if (!File.Exists(file)) return null;
            var value = File.ReadAllText(file).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Menanyakan izin kendali kepada mahasiswa dan menunggu jawabannya.
    ///
    /// Batas waktunya disengaja. Permintaan yang menggantung selamanya berarti
    /// admin menunggu tanpa tahu apakah mahasiswa menolak, sedang berpikir,
    /// atau meninggalkan mejanya. Habisnya waktu diperlakukan sebagai
    /// penolakan: mengambil alih layar orang yang tidak ada di tempat justru
    /// keadaan yang paling perlu dihindari.
    /// </summary>
    public Task<bool> AskRemoteConsent(string issuedBy, bool allowInput, TimeSpan timeout)
    {
        var decision = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.Invoke(() =>
        {
            _consent = decision;
            Push(new
            {
                type = "remoteConsent",
                issuedBy,
                allowInput,
                seconds = (int)timeout.TotalSeconds,
            });
        });

        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            if (decision.TrySetResult(false))
                Dispatcher.Invoke(() => Push(new { type = "remoteConsentClosed" }));
        });

        return decision.Task;
    }

    /// <summary>Permintaan izin yang sedang ditampilkan, null bila tidak ada.</summary>
    private TaskCompletionSource<bool>? _consent;

    /// <summary>
    /// Mengabarkan siapa yang sedang memakai PC ini, atau tidak ada siapa-siapa.
    ///
    /// YANG MENENTUKAN "SUDAH MASUK" ADALAH NAMA, BUKAN NIM.
    ///
    /// Versi sebelumnya berbunyi `nim is null ? null : ...`, dan itu benar
    /// selama satu-satunya cara masuk adalah dengan NIM. Begitu tamu ada, tamu
    /// tidak punya NIM — jadi ia masuk, agent mencatatnya masuk, panel
    /// menampilkannya masuk, dan halaman diberi tahu bahwa tidak ada siapa-siapa
    /// di PC ini.
    ///
    /// Akibatnya bukan sekadar layar yang salah. Halaman melihat dirinya belum
    /// masuk, mencoba masuk lagi, dan berputar tanpa henti — satu baris log per
    /// putaran, secepat pipa sanggup membawanya.
    /// </summary>
    public void PushStudent(string? nim, string? displayName, bool isGuest = false) =>
        Dispatcher.Invoke(() => Push(new
        {
            type = "student",
            student = displayName is null ? null : new { nim, displayName, isGuest },
        }));

    public void PushChat(string displayName, string text, bool fromAdmin, bool toEveryone = false) =>
        Dispatcher.Invoke(() => Push(new
        {
            type = "chat", displayName, text, fromAdmin, toEveryone,
            sentAt = DateTimeOffset.Now,
        }));

    /// <summary>
    /// Mengabarkan sesi ujian yang sedang berjalan. Null berarti tidak ada.
    ///
    /// Judulnya ikut supaya papan di layar mahasiswa dapat menyebutkan sesi apa
    /// yang berjalan. "Mode ujian aktif" tanpa nama terbaca seperti keadaan
    /// sistem; "Kuis Minggu 5 · aktivitas dicatat" terbaca seperti sesuatu yang
    /// dimulai seseorang dan akan berakhir.
    /// </summary>
    public void PushExam(string? sessionTitle) =>
        Dispatcher.Invoke(() => Push(new { type = "exam", title = sessionTitle }));

    public void PushChatCleared() => Dispatcher.Invoke(() => Push(new { type = "chatCleared" }));

    public void PushHelp(bool raised) => Dispatcher.Invoke(() =>
    {
        // Tombol bantuan kini hanya ada di antarmuka web, dan antarmuka itu
        // tidak pernah tertutupi lagi — jadi tidak ada salinan kedua yang
        // perlu diselaraskan.
        Push(new { type = "help", raised });
    });

    /// <summary>
    /// Memindai folder tujuan lalu mengirim seluruh isinya ke UI.
    ///
    /// Dipanggil setiap kali halaman siap, bukan hanya sekali seumur proses.
    /// Tanpa ini, daftar berkas hanya berisi yang tiba SELAMA aplikasi berjalan:
    /// mahasiswa yang menutup lalu membuka Retriva melihat daftar kosong padahal
    /// berkasnya masih utuh di disk — dan menyimpulkan berkasnya hilang.
    ///
    /// Berkas .part sengaja dilewati. Itu unduhan yang belum selesai atau gagal
    /// di tengah jalan, dan menampilkannya berarti mengarahkan mahasiswa membuka
    /// berkas yang tidak lengkap.
    /// </summary>
    public void PushInbox() => Dispatcher.Invoke(() =>
    {
        List<object> items = [];

        try
        {
            var dir = new DirectoryInfo(FileDownloaderInbox);
            if (dir.Exists)
            {
                items = dir.EnumerateFiles()
                    .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    // Menaik menurut waktu: UI menaruh yang baru di atas, jadi
                    // urutan menaik di sini menghasilkan yang terbaru di puncak.
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .Select(object (f) => new
                    {
                        fileName = f.Name,
                        savedPath = f.FullName,
                        sizeBytes = f.Length,
                        at = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                    })
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostic?.Invoke($"[berkas] gagal memindai folder: {ex.Message}");
        }

        Diagnostic?.Invoke($"[berkas] {items.Count} berkas ditemukan di {FileDownloaderInbox}");
        Push(new { type = "inbox", files = items });
    });

    public void PushFile(string fileName, string savedPath, long sizeBytes) =>
        Dispatcher.Invoke(() => Push(new
        {
            type = "file", fileName, savedPath, sizeBytes, at = DateTimeOffset.Now,
        }));

    /* ---------------- lapisan siaran ---------------- */

    /// <summary>
    /// Menyalakan penerima siaran lalu memberi tahu UI.
    ///
    /// Tidak ada jendela baru dan tidak ada lapisan yang menutupi apa pun.
    /// Yang terjadi hanya: byte mulai mengalir masuk, dan halaman diberi tahu
    /// bahwa alamat frame kini berisi sesuatu. Mahasiswa tetap dapat membuka
    /// menu mana pun sambil menonton.
    /// </summary>
    public void ShowBroadcast(string group, int port, string sourceLabel, bool fullscreen)
    {
        Dispatcher.Invoke(() =>
        {
            StopBroadcast();
            _frames = 0;
            _latestFrame = [];

            _receiver = new FrameReceiver(port, group);
            _receiver.FrameReady += OnFrameReady;
            _receiver.Diagnostic += m => Diagnostic?.Invoke(m);
            _receiver.Start();

            Diagnostic?.Invoke($"[siaran] bergabung ke {group}:{port} lewat "
                             + $"{_receiver.JoinedInterfaces} antarmuka"
                             + (_receiver.JoinError is { } je ? $" · GAGAL: {je}" : ""));

            /*
                Angka penerima dicatat berkala selama siaran berjalan.

                Tanpa ini, "tidak ada gambar" hanya satu keadaan. Dengan ini ia
                terpecah menjadi tiga yang menuntut perbaikan berbeda:

                  paket 0            -> multicast tidak sampai ke proses ini
                  paket > 0, frame 0 -> paket sampai tetapi tidak pernah utuh
                  frame > 0          -> frame utuh; yang gagal penggambarannya

                Ketiganya terlihat sama persis di layar mahasiswa, dan tanpa
                angka ini penelusurannya hanya bisa menebak yang mana.
            */
            _statsTimer.Start();

            Push(new
            {
                type = "broadcast",
                active = true,
                source = sourceLabel,
                url = FrameUrl,
                fullscreen,
            });

            /*
                YANG DIBESARKAN ADALAH TAMPILAN SIARANNYA, BUKAN APLIKASINYA.

                Halaman yang mengurus itu — ia sudah punya mode layar penuh
                siaran, overlay yang biasanya dibuka mahasiswa dengan mengklik
                pratinjau. Bendera fullscreen diteruskan ke sana, dan hasilnya
                layar berisi gambar siaran saja: tanpa menu, tanpa pratinjau
                kecil di sudut.

                Yang dikerjakan di sini hanya memastikan jendelanya TERLIHAT.
                Siaran layar penuh di dalam jendela yang terminimalkan, atau
                yang tertutup jendela lain, tidak menampilkan apa pun ke
                proyektor — dan dosen akan menyimpulkan siarannya gagal.

                Jendela tidak dipaksa maximized: ukurannya urusan orang yang
                memakai PC itu, dan PC proyektor biasanya memang sudah penuh.
            */
            if (fullscreen)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

                Topmost = true;
                Activate();
                Topmost = false;
            }

            Diagnostic?.Invoke($"[siaran] menerima dari {group}:{port} · sumber {sourceLabel}");
        });
    }

    private void OnFrameReady(ReceivedFrame frame)
    {
        // Byte disimpan langsung tanpa menyentuh thread UI. Penukaran referensi
        // bersifat atomik, jadi pembaca di thread lain tidak pernah melihat
        // gambar yang setengah tertulis.
        _latestFrame = frame.Jpeg;

        var n = System.Threading.Interlocked.Increment(ref _frames);

        // Frame pertama dicatat, dan ukurannya diberitahukan ke UI supaya
        // rasio aspek pratinjau mengikuti layar dosen yang sebenarnya.
        if (n == 1)
        {
            Diagnostic?.Invoke($"[siaran] frame pertama diterima · {frame.Width}x{frame.Height}");
            Dispatcher.BeginInvoke(() => Push(new
            {
                type = "broadcastSize",
                width = (int)frame.Width,
                height = (int)frame.Height,
            }));
        }
    }

    public void HideBroadcast() => Dispatcher.Invoke(() =>
    {
        var seen = _frames;
        StopBroadcast();
        _latestFrame = [];
        Push(new { type = "broadcast", active = false });
        Diagnostic?.Invoke($"[siaran] dihentikan setelah {seen} frame");
    });

    private void StopBroadcast()
    {
        _statsTimer.Stop();
        if (_receiver is null) return;
        _receiver.FrameReady -= OnFrameReady;
        _receiver.Dispose();
        _receiver = null;
    }

    private sealed record UiMessage
    {
        public string? Type { get; init; }
        public string? Nim { get; init; }
        public string? DisplayName { get; init; }
        public string? Text { get; init; }
        public string? Path { get; init; }
        public bool? Raised { get; init; }

        /// <summary>Jawaban mahasiswa atas permintaan kendali jarak jauh.</summary>
        public bool? Granted { get; init; }

        /// <summary>Masuk sebagai tamu, tanpa NIM.</summary>
        public bool? Guest { get; init; }

        /// <summary>Tautan yang ditekan mahasiswa di dalam pesan.</summary>
        public string? Url { get; init; }

        /// <summary>Keadaan mode presentasi yang dikabarkan halaman.</summary>
        public bool? On { get; init; }

        /// <summary>
        /// Jendela harus berada di atas jendela lain.
        ///
        /// Benar hanya bila layar penuh dinyalakan oleh siaran di PC proyektor.
        /// Mahasiswa yang membukanya sendiri tetap dapat berpindah aplikasi.
        /// </summary>
        public bool? OnTop { get; init; }
    }
}
