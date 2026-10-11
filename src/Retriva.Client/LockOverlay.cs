using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Retriva.Client;

/// <summary>
/// Layar terkunci yang ditampilkan di sesi pengguna.
///
/// Menutupi seluruh monitor sekaligus dengan memakai batas layar virtual,
/// bukan satu jendela per monitor — lebih sederhana dan tidak meninggalkan
/// celah di antara layar saat susunannya tidak rata.
///
/// Ini BUKAN Lock Workstation milik Windows. Sesi pengguna tetap terbuka,
/// sehingga admin masih dapat melihat layar dan mengambil alih kontrol.
/// </summary>
public sealed class LockOverlay : Window
{
    private readonly TextBlock _clock = new()
    {
        FontSize = 58,
        FontWeight = FontWeights.Light,
        Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xEB)),
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 26, 0, 30),
    };

    private readonly TextBlock _message = new()
    {
        FontSize = 17,
        Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xEB)),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 520,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _issuer = new()
    {
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>
    /// Pintu darurat khusus pengembangan: Ctrl+Shift+Alt+U melepas kunci.
    ///
    /// Hanya aktif bila variabel lingkungan RETRIVA_DEV_RESCUE bernilai 1,
    /// sehingga tidak pernah ada di pemasangan lab. Tanpa ini, menguji kunci
    /// layar di satu mesin berarti mengunci diri sendiri tanpa jalan keluar
    /// selain Task Manager.
    /// </summary>
    public event Action? RescueRequested;

    private static bool RescueEnabled =>
        Environment.GetEnvironmentVariable("RETRIVA_DEV_RESCUE") == "1";

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _assertTop = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _allowClose;

    public LockOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
        Cursor = System.Windows.Input.Cursors.None;

        CoverAllScreens();

        var mark = BuildMark();

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (RescueEnabled)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "MODE PENGEMBANGAN — Ctrl+Shift+Alt+U untuk melepas kunci",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x86, 0x19)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18),
            });
        }
        stack.Children.Add(mark);
        stack.Children.Add(_clock);
        stack.Children.Add(_message);
        stack.Children.Add(_issuer);
        stack.Children.Add(new TextBlock
        {
            Text = "Komputer ini sedang dikendalikan dari panel admin. Tunggu instruksi selanjutnya.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 460,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 34, 0, 0),
        });

        Content = new Grid { Children = { stack } };

        UpdateClock();
        _tick.Tick += (_, _) => UpdateClock();

        // Aplikasi lain kadang merebut urutan tumpukan. Menegaskan ulang
        // topmost secara berkala jauh lebih murah daripada melacak setiap
        // jendela yang mungkin muncul di atasnya.
        _assertTop.Tick += (_, _) =>
        {
            Topmost = false;
            Topmost = true;
            Activate();
        };

        KeyDown += (_, e) =>
        {
            if (!RescueEnabled || e.Key != System.Windows.Input.Key.U) return;

            var mods = System.Windows.Input.Keyboard.Modifiers;
            var wanted = System.Windows.Input.ModifierKeys.Control
                       | System.Windows.Input.ModifierKeys.Shift
                       | System.Windows.Input.ModifierKeys.Alt;
            if (mods == wanted) RescueRequested?.Invoke();
        };

        Closing += (_, e) => e.Cancel = !_allowClose;
        Deactivated += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(Activate); };

        // Handle jendela baru ada setelah SourceInitialized.
        SourceInitialized += (_, _) =>
        {
            // Sengaja TIDAK aktif secara bawaan.
            //
            // WDA_EXCLUDEFROMCAPTURE seharusnya hanya menyembunyikan jendela dari
            // API tangkapan layar, tetapi pada sebagian kombinasi GPU dan driver
            // tampilan, jalur monitor fisik ikut dianggap capture sehingga overlay
            // hilang sama sekali. Itu terjadi di mesin pengembangan proyek ini.
            //
            // Tujuan aslinya — admin tetap melihat desktop asli di balik layar
            // terkunci — kini dicapai dengan cara lain: agent membekukan pratinjau
            // pada frame terakhir sebelum dikunci. Tidak bergantung driver sama sekali.
            if (Environment.GetEnvironmentVariable("RETRIVA_OVERLAY_CAPTURE") == "hidden")
                ExcludeFromScreenCapture();
        };
    }

    /// <summary>
    /// Menyembunyikan overlay dari seluruh API tangkapan layar, tetapi tetap
    /// terlihat di monitor fisik.
    ///
    /// Akibatnya mahasiswa melihat layar terkunci, sementara admin melihat
    /// desktop yang sesungguhnya ada di baliknya. Ini bukan sekadar kenyamanan:
    /// tanpa ini, 40 pratinjau di dashboard berubah menjadi 40 layar kunci
    /// yang identik, dan admin kehilangan seluruh gambaran situasi kelas
    /// justru pada saat ia paling membutuhkannya.
    ///
    /// WDA_EXCLUDEFROMCAPTURE tersedia sejak Windows 10 versi 2004. Pada versi
    /// lebih lama panggilan ini gagal tanpa efek, dan overlay ikut tertangkap —
    /// perilaku lama, bukan kerusakan.
    /// </summary>
    private void ExcludeFromScreenCapture()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) { Diagnostic?.Invoke("handle jendela belum ada"); return; }

            var ok = SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
            Diagnostic?.Invoke(ok
                ? "overlay dikecualikan dari tangkapan layar"
                : $"pengecualian tangkapan layar gagal (error {Marshal.GetLastWin32Error()})");
        }
        catch (EntryPointNotFoundException)
        {
            Diagnostic?.Invoke("Windows terlalu lama untuk WDA_EXCLUDEFROMCAPTURE");
        }
    }

    /// <summary>Pesan teknis untuk ditampilkan di jendela status klien.</summary>
    public event Action<string>? Diagnostic;

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /*
        Lambang di layar terkunci memakai logo ADMIN, bukan logo klien.

        Bukan kekeliruan pemilihan berkas: layar ini bukan bagian dari aplikasi
        mahasiswa melainkan wujud dari perintah yang datang dari panel admin.
        Menampilkan logo klien akan terbaca seolah aplikasi mahasiswa yang
        mengunci dirinya sendiri, padahal yang penting justru sebaliknya —
        mahasiswa perlu tahu ini datang dari orang lain, dan kepada siapa ia
        harus bertanya.
    */
    private static UIElement BuildMark()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "admin", "mark.png");

        // Didekode pada ukuran tampil. Tanpa itu logo 1000 piksel disusutkan
        // hampir dua puluh kali oleh penyaringan bawaan WPF, dan hasilnya lembek
        // justru di layar yang paling lama dipandangi mahasiswa.
        var image = Retriva.Ui.ImageAssets.TryLoad(path, MarkSize);
        if (image is not null)
        {
            image.HorizontalAlignment = HorizontalAlignment.Center;
            return image;
        }

        // Layar terkunci tidak boleh gagal tampil hanya karena berkas gambar
        // bermasalah — kegagalannya berarti mahasiswa melihat layar hitam tanpa
        // penjelasan apa pun, dan itu jauh lebih buruk daripada logo yang salah.
        return new Border
        {
            Width = MarkSize, Height = MarkSize, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(0xD4, 0x0D, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "R", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private const double MarkSize = 54;

    private void CoverAllScreens()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void UpdateClock() => _clock.Text = DateTime.Now.ToString("HH:mm");

    /// <summary>Mengembalikan true hanya bila jendela benar-benar terlihat sesudahnya.</summary>
    public bool ShowLocked(string message, string issuedBy)
    {
        try
        {
            _message.Text = message;
            _issuer.Text = $"Dikunci oleh {issuedBy}";
            CoverAllScreens();

            _tick.Start();
            _assertTop.Start();
            Show();
            Activate();
            Focus();

            Diagnostic?.Invoke(
                $"overlay: {Width:F0}x{Height:F0} di ({Left:F0},{Top:F0}) · terlihat={IsVisible}");
            return IsVisible;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"overlay gagal tampil: {ex.GetType().Name} — {ex.Message}");
            return false;
        }
    }

    public bool HideLocked()
    {
        try
        {
            _tick.Stop();
            _assertTop.Stop();
            Hide();
            return !IsVisible;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"overlay gagal disembunyikan: {ex.Message}");
            return false;
        }
    }

    public void AllowClose()
    {
        _allowClose = true;
        _tick.Stop();
        _assertTop.Stop();
        Close();
    }
}
