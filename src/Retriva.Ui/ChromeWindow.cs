using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Retriva.Ui;

/// <summary>
/// Jendela dengan bingkai yang digambar sendiri.
///
/// WindowChrome dipakai alih-alih WindowStyle.None polos karena ia
/// mempertahankan seluruh perilaku jendela yang orang harapkan tanpa harus
/// ditulis ulang: seret untuk memindah, klik ganda untuk memaksimalkan, seret
/// ke tepi untuk snap, dan tepi yang dapat ditarik untuk mengubah ukuran.
/// Menulis ulang perilaku itu sendiri hampir selalu menghasilkan jendela yang
/// terasa "hampir benar" — dan "hampir benar" pada bingkai jendela sangat
/// mengganggu karena tangan sudah hafal cara memakainya.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class ChromeWindow : Window
{
    protected const double TitleBarHeight = 36;

    private static readonly Color Shell = Color.FromRgb(0x1B, 0x1B, 0x1B);
    private static readonly Color Danger = Color.FromRgb(0xD4, 0x0D, 0x00);

    private readonly TextBlock _maxGlyph = new();
    private readonly DockPanel _root = new();

    /// <summary>Bilah judul, disembunyikan saat mode presentasi.</summary>
    private UIElement? _titleBar;

    /// <summary>
    /// Mode presentasi: jendela menutupi SELURUH layar, termasuk taskbar.
    ///
    /// Dipakai PC proyektor saat siaran berjalan. Tanpa ini, gambar siaran
    /// terpotong bilah judul aplikasi di atas dan taskbar Windows di bawah —
    /// dua garis yang tidak berarti apa-apa bagi mahasiswa di belakang kelas
    /// dan membuat presentasi terlihat seperti tangkapan layar, bukan seperti
    /// presentasi.
    /// </summary>
    private bool _presenting;

    /// <summary>Nama folder aset milik aplikasi ini: "client" atau "admin".</summary>
    private readonly string _assetFolder;

    protected ChromeWindow(string appName, string subtitle, string assetFolder)
    {
        _assetFolder = assetFolder;

        Title = $"Retriva — {subtitle}";
        Background = new SolidColorBrush(Shell);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;

        /*
            Dibuka langsung dalam keadaan dimaksimalkan.

            Bukan sekadar selera: seluruh tata letak aplikasi ini dirancang untuk
            layar penuh — grid 40 unit di panel admin, dan pratinjau siaran di
            sisi mahasiswa. Pada jendela kecil keduanya menjadi sempit sampai
            sulit dipakai, dan tidak ada yang sempat memperbesarnya sendiri di
            tengah praktikum yang sudah dimulai.

            Ukuran normal di bawah ini tetap ditetapkan karena dipakai saat
            jendela dikembalikan dari maximized.
        */
        WindowState = WindowState.Maximized;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        _titleBar = BuildTitleBar(appName, subtitle);
        DockPanel.SetDock(_titleBar, Dock.Top);
        _root.Children.Add(_titleBar);

        StateChanged += (_, _) => SyncMaxGlyph();

        // Diselaraskan sekali di awal juga. Jendela dibuka dalam keadaan
        // maximized, sedangkan bilah dibangun dengan glyph "maksimalkan" —
        // tanpa baris ini tombolnya menampilkan lambang yang berlawanan dengan
        // keadaan sesungguhnya sampai orang menekannya sekali.
        SyncMaxGlyph();

        SourceInitialized += OnSourceInitialized;
        LoadAppIcon();
    }

    private void SyncMaxGlyph() =>
        _maxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    /// <summary>Isi jendela di bawah bingkai. Dipanggil kelas turunan sekali saat siap.</summary>
    protected void SetBody(UIElement body)
    {
        _root.Children.Add(body);
        Content = _root;
    }

    protected string AssetPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "assets", _assetFolder, fileName);

    /// <summary>Dilaporkan ke pemanggil bila ada aset yang gagal dimuat.</summary>
    public Action<string>? AssetDiagnostic;

    /* ================= batas ukuran saat dimaksimalkan ================= */

    /*
        Jendela WindowChrome yang dimaksimalkan meluber melewati tepi layar.

        Versi pertama menambal ini dengan memberi margin tujuh piksel saat
        maximized — angka tebakan yang kebetulan mendekati benar pada satu
        penskalaan DPI dan salah pada yang lain. Akibatnya bagian bawah jendela
        terpotong, dan tombol di kaki rail — tema serta keluar — hilang sama
        sekali tanpa petunjuk apa pun bahwa ada yang terpotong.

        Perbaikan yang benar adalah memberi tahu Windows batas ukuran yang
        sesungguhnya lewat WM_GETMINMAXINFO: area kerja monitor tempat jendela
        berada, yaitu layar dikurangi taskbar. Ini juga otomatis benar pada
        monitor kedua dan pada penskalaan DPI berapa pun.
    */

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        OnWindowHandleReady(handle);
    }

    /// <summary>Kelas turunan memakai ini untuk hal yang butuh HWND, mis. pengecualian capture.</summary>
    protected virtual void OnWindowHandleReady(IntPtr handle) { }

    /// <summary>
    /// Masuk atau keluar mode presentasi.
    ///
    /// Tiga hal berubah bersama, dan ketiganya perlu:
    ///   - bilah judul disembunyikan, kalau tidak gambar terpotong di atas
    ///   - batas maksimalkan menjadi seluruh layar, kalau tidak taskbar tetap ada
    ///   - jendela dijadikan Normal sesaat lalu Maximized lagi, karena Windows
    ///     tidak menanyakan ulang WM_GETMINMAXINFO pada jendela yang sudah
    ///     maximized — tanpa langkah itu batas barunya tidak pernah dipakai
    /// </summary>
    /// <param name="stayOnTop">
    /// MEMBEDAKAN DUA PEMAKAIAN YANG TERLIHAT SAMA.
    ///
    /// PC proyektor: true. Tidak ada yang boleh menutupi presentasi — notifikasi,
    /// jendela yang tidak sengaja terbuka, apa pun. Taskbar ikut tertutup.
    ///
    /// PC mahasiswa: false. Layar penuh di sini hanya berarti "gambarnya besar",
    /// bukan "kunci layar saya". Mahasiswa yang sedang mengikuti demo sambil
    /// mengetik kode harus dapat berpindah ke editornya dan kembali lagi tanpa
    /// kehilangan tampilannya — jendela tetap layar penuh, hanya berada di
    /// belakang. Taskbar tetap terlihat di atasnya, dan itu memang jalan
    /// berpindahnya.
    ///
    /// Menyamakan keduanya merugikan salah satu: proyektor yang tertimpa
    /// notifikasi, atau mahasiswa yang harus menutup siaran setiap kali ingin
    /// menulis satu baris kode.
    /// </param>
    public void SetPresentationMode(bool on, bool stayOnTop)
    {
        // Topmost tetap diselaraskan meski mode tidak berubah: mahasiswa dapat
        // membuka layar penuh sendiri lalu siaran proyektor menyala di unit yang
        // sama, dan sebaliknya.
        Topmost = on && stayOnTop;

        if (_presenting == on) return;
        _presenting = on;

        if (_titleBar is not null)
            _titleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        /*
            Batas yang dipakai berbeda menurut mode.

            Biasa      : rcWork — layar dikurangi taskbar, supaya jendela yang
                         dimaksimalkan tidak menutupi taskbar seperti aplikasi
                         yang rusak.
            Presentasi : rcMonitor — SELURUH layar, taskbar ikut tertutup. Itu
                         yang membedakan "jendela besar" dari "presentasi", dan
                         yang dilakukan PowerPoint maupun pemutar video.
        */
        var bounds = _presenting ? info.rcMonitor : info.rcWork;

        // Posisi dan ukuran dinyatakan relatif terhadap sudut monitor, bukan
        // terhadap desktop virtual — itu sebabnya rcMonitor dikurangkan.
        mmi.ptMaxPosition.X = bounds.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = bounds.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = bounds.Right - bounds.Left;
        mmi.ptMaxSize.Y = bounds.Bottom - bounds.Top;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /* ================= bingkai ================= */

    private void LoadAppIcon()
    {
        var path = AssetPath("app.ico");
        var icon = ImageAssets.TryLoadIcon(path);

        if (icon is null)
        {
            if (File.Exists(path)) AssetDiagnostic?.Invoke($"[ikon] gagal memuat {path}");
            return;
        }
        Icon = icon;
    }

    private UIElement BuildTitleBar(string appName, string subtitle)
    {
        var bar = new Grid { Height = TitleBarHeight, Background = new SolidColorBrush(Shell) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
        };

        left.Children.Add(BuildMark());
        left.Children.Add(new TextBlock
        {
            Text = appName,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xEB)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });

        Grid.SetColumn(left, 0);
        bar.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        WindowChrome.SetIsHitTestVisibleInChrome(buttons, true);

        buttons.Children.Add(CaptionButton(Glyph("\uE921"), () => WindowState = WindowState.Minimized, false));

        _maxGlyph.Text = "\uE922";
        buttons.Children.Add(CaptionButton(_maxGlyph, () =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized, false));

        buttons.Children.Add(CaptionButton(Glyph("\uE8BB"), Close, danger: true));

        Grid.SetColumn(buttons, 2);
        bar.Children.Add(buttons);

        return bar;
    }

    /// <summary>
    /// Lambang di bingkai tampil pada 18 piksel. Bila berkasnya belum ada,
    /// penanda sementara dipakai supaya jelas ini bukan desain final.
    /// </summary>
    private UIElement BuildMark()
    {
        var path = AssetPath("mark.png");

        // Didekode pada ukuran tampil, bukan dimuat utuh lalu disusutkan.
        // Perbedaannya besar: logo 1000 piksel yang dikecilkan ke 18 dengan
        // penyaringan bawaan tampak lembek, dan orang akan menyangka logonya
        // yang bermasalah padahal berkasnya tajam sempurna.
        var image = ImageAssets.TryLoad(path, MarkSize);
        if (image is not null)
        {
            image.VerticalAlignment = VerticalAlignment.Center;
            image.Margin = new Thickness(0, 0, 9, 0);
            return image;
        }

        if (File.Exists(path)) AssetDiagnostic?.Invoke($"[lambang] gagal memuat {path}");

        return new Border
        {
            Width = MarkSize,
            Height = MarkSize,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Danger),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock
            {
                Text = "R",
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private const double MarkSize = 18;

    private static TextBlock Glyph(string glyph) => new() { Text = glyph };

    /// <summary>
    /// Tombol jendela berukuran 46x36 dengan sorot abu-abu, merah khusus tutup.
    /// Ukuran itu bukan angka acak: ia sama dengan bawaan Windows, dan tangan
    /// sudah hafal letaknya — mengubahnya membuat orang meleset.
    /// </summary>
    private static Button CaptionButton(TextBlock glyph, Action onClick, bool danger)
    {
        glyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
        glyph.FontSize = 10;
        glyph.Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;

        var hover = danger ? Danger : Color.FromRgb(0x33, 0x33, 0x33);

        var button = new Button
        {
            Width = 46,
            Height = TitleBarHeight,
            Content = glyph,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
        };

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;

        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(hover);
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => onClick();

        return button;
    }

    /* ================= interop ================= */

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
