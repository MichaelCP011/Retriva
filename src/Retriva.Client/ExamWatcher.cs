using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Retriva.Shared.Protocol;

namespace Retriva.Client;

/// <summary>
/// Memantau aktivitas selama sesi ujian: jendela aktif, papan klip, dan USB.
///
/// HIDUP HANYA SELAMA SESI BERJALAN, DAN ITU SYARAT UTAMANYA.
///
/// Pemantauan yang tertinggal menyala setelah sesi ditutup adalah kegagalan
/// paling buruk yang mungkin terjadi di berkas ini. Ia tidak memunculkan galat,
/// tidak melambatkan apa pun yang terasa, dan tidak terlihat di layar mana pun —
/// papan "mode ujian" sudah hilang, jadi mahasiswa mengira dirinya tidak lagi
/// diawasi sementara pengamatannya terus mengalir. Yang rusak bukan fiturnya,
/// melainkan janji yang sudah dibuat kepada orang di depan layar.
///
/// Karena itu seluruh sumber daya di sini dilepas di satu tempat, Stop(), dan
/// Stop() aman dipanggil berulang. Tidak ada jalur lain yang boleh memasang hook
/// atau pendengar papan klip.
///
/// KLIEN, BUKAN AGENT, yang memantau. Agent adalah layanan di Session 0: ia
/// tidak dapat membaca jendela aktif sesi pengguna, tidak dapat membuka papan
/// klip, dan tidak menerima WM_DEVICECHANGE. Pembagian yang sama berlaku untuk
/// kunci layar dan tangkapan layar.
/// </summary>
public sealed class ExamWatcher : IDisposable
{
    private readonly Action<ExamEventKind, string> _observe;
    private readonly Action<string> _log;

    private DispatcherTimer? _windowPoll;
    private HwndSource? _source;
    private nint _clipboardOwner;
    private bool _clipboardListening;

    private string _lastWindowTitle = "";
    private HashSet<char> _knownDrives = [];

    public bool Running { get; private set; }

    /// <summary>Judul sesi yang sedang berjalan, kosong bila tidak ada.</summary>
    public string SessionTitle { get; private set; } = "";

    public ExamWatcher(Action<ExamEventKind, string> observe, Action<string> log)
    {
        _observe = observe;
        _log = log;
    }

    /// <summary>
    /// Mulai memantau. Jendela dipakai untuk memasang pendengar papan klip dan
    /// WM_DEVICECHANGE, karena keduanya menuntut handle jendela yang nyata.
    /// </summary>
    public void Start(Window window, string sessionTitle)
    {
        if (Running) Stop();

        SessionTitle = sessionTitle;
        Running = true;

        /*
            Jendela aktif DIPANTAU DENGAN POLLING, bukan dengan SetWinEventHook.

            Hook peristiwa lebih rapi di atas kertas dan lebih berbahaya di sini:
            ia memanggil balik ke dalam proses ini pada setiap perubahan fokus di
            seluruh desktop, dan sebuah callback yang lambat atau melempar
            pengecualian akan terasa oleh SETIAP aplikasi yang sedang dipakai
            mahasiswa. Pada mesin yang sedang menjalankan ujian, itu risiko yang
            tidak sebanding dengan kerapiannya.

            Setengah detik cukup: yang ingin diketahui adalah "aplikasi apa yang
            dibuka", bukan setiap kedipan fokus. Pergantian yang lebih cepat dari
            itu justru derau yang membuat lini masanya tidak terbaca.
        */
        _windowPoll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _windowPoll.Tick += (_, _) => PollForegroundWindow();
        _windowPoll.Start();

        _knownDrives = CurrentRemovableDrives();

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            _source = source;
            _clipboardOwner = source.Handle;
            source.AddHook(WndProc);

            if (AddClipboardFormatListener(source.Handle)) _clipboardListening = true;
            else _log("[ujian] pendengar papan klip GAGAL dipasang");
        }
        else
        {
            // Dicatat, bukan didiamkan. Tanpa handle jendela, dua dari tiga
            // sinyal tidak berjalan — dan lini masa yang hanya berisi pergantian
            // jendela akan terbaca seperti mahasiswa yang tidak pernah menyalin
            // apa pun, bukan seperti pemantauan yang setengah jadi.
            _log("[ujian] jendela belum punya handle — papan klip dan USB tidak dipantau");
        }

        _log($"[ujian] pemantauan mulai · {sessionTitle}");
    }

    /// <summary>Menghentikan pemantauan dan melepas seluruh sumber dayanya.</summary>
    public void Stop()
    {
        if (!Running && _source is null && _windowPoll is null) return;

        _windowPoll?.Stop();
        _windowPoll = null;

        if (_clipboardListening && _clipboardOwner != 0)
        {
            RemoveClipboardFormatListener(_clipboardOwner);
            _clipboardListening = false;
        }
        _clipboardOwner = 0;

        _source?.RemoveHook(WndProc);
        _source = null;

        _lastWindowTitle = "";
        _knownDrives = [];
        SessionTitle = "";

        if (Running) _log("[ujian] pemantauan berhenti · sumber daya dilepas");
        Running = false;
    }

    public void Dispose() => Stop();

    private void PollForegroundWindow()
    {
        if (!Running) return;

        var handle = GetForegroundWindow();
        if (handle == 0) return;

        var title = WindowTitle(handle);
        if (string.IsNullOrWhiteSpace(title)) return;
        if (title == _lastWindowTitle) return;

        _lastWindowTitle = title;
        _observe(ExamEventKind.Window, title);
    }

    private static string WindowTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return "";

        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (!Running) return 0;

        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                OnClipboardChanged();
                break;

            case WM_DEVICECHANGE:
                // Diperiksa dengan membandingkan daftar drive, bukan dengan
                // membongkar lParam. Struktur DEV_BROADCAST_VOLUME memberi
                // bitmask drive, tetapi menafsirkannya salah menghasilkan huruf
                // drive yang keliru — dan perbandingan daftar tidak dapat salah
                // tentang apa yang benar-benar ada di sistem.
                OnDeviceChanged();
                break;
        }

        return 0;
    }

    /*
        Papan klip: HANYA PERISTIWANYA, tanpa isinya.

        Yang tercatat adalah bahwa penyalinan terjadi, berapa besar, dan dari
        jendela apa. Isinya sengaja tidak pernah dibaca ke dalam catatan.

        Alasannya bukan kesulitan teknis — membaca teks papan klip justru mudah.
        Alasannya adalah apa yang akan tersimpan: sepanjang ujian, mahasiswa
        menyalin kata sandi, pesan pribadi, dan hal yang tidak berhubungan dengan
        ujian sama sekali. Semua itu akan mendarat di berkas SQLite tanpa
        enkripsi di komputer asisten, bertahan sampai ada yang menghapusnya, dan
        tidak ada satu pun keuntungan pengawasan yang sebanding dengan itu.

        Ukuran dan asalnya sudah cukup untuk melihat pola. Delapan penyalinan
        panjang dari peramban dalam dua menit terbaca jelas tanpa satu karakter
        pun disimpan.
    */
    private void OnClipboardChanged()
    {
        var source = WindowTitle(GetForegroundWindow());
        var shape = DescribeClipboardShape();

        _observe(ExamEventKind.Clipboard, string.IsNullOrWhiteSpace(source)
            ? shape
            : $"{shape} · dari {source}");
    }

    /// <summary>Bentuk isi papan klip — jenis dan ukuran, bukan isinya.</summary>
    private string DescribeClipboardShape()
    {
        try
        {
            // Sekali coba, tanpa pengulangan. Papan klip dikunci proses lain
            // secara berkala, dan mengulang di sini berarti menahan utas UI
            // klien pada saat mahasiswa sedang bekerja.
            if (Clipboard.ContainsText())
            {
                var length = Clipboard.GetText()?.Length ?? 0;
                return $"menyalin teks {length:N0} karakter";
            }

            if (Clipboard.ContainsImage()) return "menyalin gambar";
            if (Clipboard.ContainsFileDropList())
            {
                var count = Clipboard.GetFileDropList().Count;
                return $"menyalin {count} berkas";
            }

            return "papan klip berubah";
        }
        catch (Exception ex)
        {
            // Papan klip yang tidak dapat dibaca tetap dicatat sebagai
            // perubahan. Membuang peristiwanya berarti lini masa kehilangan
            // penyalinan yang benar-benar terjadi.
            _log($"[ujian] papan klip tidak terbaca: {ex.Message}");
            return "papan klip berubah";
        }
    }

    private void OnDeviceChanged()
    {
        var now = CurrentRemovableDrives();

        foreach (var drive in now.Except(_knownDrives))
            _observe(ExamEventKind.Usb, $"penyimpanan {drive}: dicolokkan");

        foreach (var drive in _knownDrives.Except(now))
            _observe(ExamEventKind.Usb, $"penyimpanan {drive}: dilepas");

        _knownDrives = now;
    }

    /// <summary>
    /// Drive lepasan yang sedang ada.
    ///
    /// DriveType.Removable saja, bukan seluruh drive. Drive jaringan yang
    /// tersambung dan terputus sendiri sepanjang hari akan mengisi lini masa
    /// dengan peristiwa yang tidak dilakukan mahasiswa mana pun.
    /// </summary>
    private HashSet<char> CurrentRemovableDrives()
    {
        try
        {
            return [.. DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                .Select(d => d.Name[0])];
        }
        catch (Exception ex)
        {
            _log($"[ujian] daftar drive tidak terbaca: {ex.Message}");
            return _knownDrives;
        }
    }

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_DEVICECHANGE = 0x0219;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);
}
