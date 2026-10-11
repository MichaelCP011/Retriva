using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Retriva.Media;
using Retriva.Shared.Broadcast;
using Retriva.Shared.Local;

namespace Retriva.Client;

/// <summary>
/// Sisi mahasiswa dari kendali jarak jauh: mengirim layarnya ke admin, menerima
/// suntikan input, dan menahan mouse serta keyboard fisik selama sesi berjalan.
///
/// HARUS dipanggil dari thread UI. Hook tingkat rendah terpasang pada thread
/// yang memanggil SetWindowsHookEx, dan thread itu wajib memompa pesan Windows —
/// kalau tidak, hook-nya terpasang tetapi tidak pernah dipanggil, sehingga
/// pemblokiran tampak aktif sementara input fisik tetap lolos.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteSessionHost : IDisposable
{
    private readonly InputInjector _injector = new();
    private readonly RemoteInputBlocker _blocker = new();
    private BroadcastSession? _stream;
    private RemoteIndicator? _indicator;

    /// <summary>Sesi berakhir dari sisi mahasiswa, dengan alasannya.</summary>
    public event Action<string>? Ended;

    public bool Active => _stream is not null;

    /// <summary>
    /// Melewati pemblokiran input saat menguji di satu mesin.
    ///
    /// Variabel yang sama dipakai pintu darurat overlay kunci, karena
    /// persoalannya sama persis: pengaman yang benar di lab justru mengunci
    /// orang yang sedang mengembangkannya. Tidak pernah aktif di lab.
    /// </summary>
    public static bool DevRescue =>
        Environment.GetEnvironmentVariable("RETRIVA_DEV_RESCUE") == "1";

    public RemoteSessionHost() =>
        _blocker.EscapeRequested += () => Stop("dihentikan oleh mahasiswa");

    /// <summary>
    /// Mengakhiri sesi bila jalur ke agent putus.
    ///
    /// Ini sakelar orang-mati. Kalau agent atau panel admin mati di tengah sesi,
    /// tidak ada lagi yang dapat mengirim perintah akhiri — dan mahasiswa
    /// tertinggal dengan mouse serta keyboard yang tertahan selamanya, tanpa
    /// tahu penyebabnya. Klien harus dapat membebaskan dirinya sendiri.
    /// </summary>
    public void OnLinkChanged(bool connected)
    {
        if (!connected && Active) Stop("jalur ke agent terputus");
    }

    /// <summary>Mengembalikan keterangan untuk ack, bukan hanya berhasil/gagal.</summary>
    public string Start(BeginRemoteSession message)
    {
        if (Active) Stop("digantikan sesi baru");

        // Layar dikirim dalam resolusi menengah. Kendali jarak jauh menuntut
        // latensi rendah lebih daripada ketajaman: yang dilihat asisten cukup
        // untuk mengarahkan kursor, dan setiap piksel tambahan menunda frame.
        _stream = new BroadcastSession(new BroadcastSettings(
            TargetWidth: 1280,
            Quality: 70,
            Fps: Math.Clamp(message.FrameRate, 5, 30),
            Port: message.Port,
            UnicastHost: message.AdminHost));

        _stream.Start();

        _indicator = new RemoteIndicator(message.IssuedByName, message.AllowInput);
        _indicator.Show();

        // Pemblokiran hanya saat admin benar-benar mengambil alih. Saat mode
        // lihat-saja, mahasiswa tetap bekerja normal — dan itulah gunanya
        // membedakan keduanya.
        //
        // Di mesin pengembangan, memblokir input berarti memblokir orang yang
        // sedang menguji: panel admin ada di layar yang sama, jadi tidak ada
        // lagi cara menekan "Akhiri sesi" dan Task Manager menjadi satu-satunya
        // jalan keluar. Sama seperti overlay kunci yang dulu menutupi panel
        // adminnya sendiri.
        // Hook TETAP dipasang di mode pengembangan, hanya tanpa menahan apa pun.
        // Dengan begitu Ctrl+Alt+Shift+S selalu tersedia, dan fitur jalan keluar
        // dapat diuji tanpa harus mengunci diri sendiri lebih dulu.
        var blocking = message.AllowInput && !DevRescue;
        if (message.AllowInput) _blocker.Install(blocking);

        var where = $"layar dikirim ke {message.AdminHost}:{message.Port}";
        if (!message.AllowInput) return $"mode lihat-saja, {where}";
        return blocking
            ? $"kendali aktif, {where}"
            : $"kendali aktif TANPA pemblokiran input (mode pengembangan), {where}";
    }

    public void Apply(InjectInput message)
    {
        if (!Active) return;
        _injector.Apply(message);
    }

    /// <summary>Alasan penolakan suntikan terakhir, untuk dilaporkan ke konsol.</summary>
    public string? LastInjectionFault => _injector.LastFault;

    public void Stop(string reason)
    {
        if (!Active) return;

        _blocker.Uninstall();

        var stream = _stream;
        _stream = null;
        _ = stream?.DisposeAsync();

        _indicator?.Close();
        _indicator = null;

        Ended?.Invoke(reason);
    }

    public void Dispose()
    {
        Stop("klien ditutup");
        _blocker.Dispose();
    }
}

/// <summary>
/// Pita pemberitahuan bahwa layar sedang dikendalikan.
///
/// Sengaja tidak dapat diklik dan tidak mengambil fokus. Kalau bisa diklik, ia
/// akan menghalangi asisten yang mengarahkan kursor ke area di bawahnya; kalau
/// mengambil fokus, ia akan mencuri ketikan yang seharusnya masuk ke aplikasi.
///
/// Juga sengaja TIDAK dikecualikan dari tangkapan layar. Asisten perlu melihat
/// pita ini di panelnya sebagai penegasan bahwa mahasiswa memang diberi tahu.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RemoteIndicator : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public RemoteIndicator(string controller, bool inputTaken)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        var accent = inputTaken
            ? Color.FromRgb(0xD4, 0x0D, 0x00)     // merah: input diambil alih
            : Color.FromRgb(0x0B, 0x6E, 0x50);    // hijau: hanya dilihat

        var text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Inlines.Add(new System.Windows.Documents.Run(
            inputTaken ? "Layar Anda sedang dikendalikan oleh " : "Layar Anda sedang dilihat oleh ")
        { FontWeight = FontWeights.Normal });
        text.Inlines.Add(new System.Windows.Documents.Run(controller) { FontWeight = FontWeights.Bold });

        if (inputTaken)
        {
            text.Inlines.Add(new System.Windows.Documents.Run(
                $"   ·   tekan {RemoteInputBlocker.EscapeHint} untuk menghentikan")
            { Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) });
        }

        Content = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(18, 9, 18, 9),
            Child = text,
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        };

        // Ditempel di tengah atas setelah ukurannya diketahui. Menghitungnya
        // sebelum layout selesai menghasilkan lebar nol dan pita yang melompat.
        ContentRendered += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 0;
        };
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
