using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Retriva.Client;

/// <summary>
/// Memblokir mouse dan keyboard fisik mahasiswa selama sesi kendali, kecuali
/// peristiwa yang kita suntikkan sendiri dan satu pintasan untuk keluar.
///
/// INI BAGIAN YANG PALING MUDAH SALAH.
///
/// Hook tingkat rendah tidak membedakan peristiwa dari perangkat fisik dengan
/// peristiwa dari SendInput. Pemblokir yang naif akan memblokir suntikan admin
/// juga, sehingga kendali jarak jauh mati total — sementara log di kedua sisi
/// menyatakan perintah terkirim, diterima, dan disuntikkan dengan sukses.
/// Kegagalan yang tidak menyisakan jejak apa pun, seperti pipa yang macet dan
/// daftar multicast yang kosong sebelumnya.
///
/// Karena itu setiap peristiwa diperiksa terhadap penanda dwExtraInfo milik
/// InputInjector. Penanda dipakai alih-alih bendera LLKHF_INJECTED karena
/// bendera itu meloloskan suntikan dari aplikasi mana pun, termasuk yang justru
/// ingin kita cegah selama sesi berlangsung.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteInputBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;      // Alt
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_S = 0x53;

    /*
        Keadaan tombol pengubah dilacak sendiri, tidak dibaca dari Windows.

        Ini bukan pilihan gaya. Hook ini menahan SEMUA tombol fisik, termasuk
        Ctrl, Alt, dan Shift. Tombol yang ditahan tidak pernah sampai ke Windows,
        sehingga GetAsyncKeyState melaporkannya tidak tertekan — dan kombinasi
        keluar menjadi mustahil terpenuhi selamanya.

        Versi pertama memakai GetAsyncKeyState dan justru karena itu jalan
        keluarnya tidak pernah berfungsi: pemblokiran kita sendiri yang
        menghancurkan keadaan yang dibutuhkan untuk membatalkannya. Hook ini
        melihat setiap peristiwa fisik sebelum menahannya, jadi ia satu-satunya
        pihak yang masih tahu tombol mana yang sedang ditekan.
    */
    private bool _ctrlDown;
    private bool _altDown;
    private bool _shiftDown;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kedua delegat WAJIB disimpan di field. Bila hanya dilewatkan sebagai
    // argumen, GC mengumpulkannya sementara Windows masih memegang pointernya,
    // dan proses crash beberapa saat kemudian di tempat yang tidak berhubungan.
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;

    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>Dipanggil saat mahasiswa menekan pintasan keluar.</summary>
    public event Action? EscapeRequested;

    /// <summary>Pintasan yang ditampilkan pada indikator; harus cocok dengan pemeriksaan di bawah.</summary>
    public const string EscapeHint = "Ctrl + Alt + Shift + S";

    public RemoteInputBlocker()
    {
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
    }

    public bool Active => _keyboardHook != IntPtr.Zero;

    /// <summary>
    /// True bila input fisik benar-benar ditahan. False berarti hook hanya
    /// mengintai kombinasi keluar dan meneruskan segalanya.
    /// </summary>
    public bool Blocking { get; private set; }

    /// <summary>
    /// Memasang hook.
    ///
    /// <paramref name="blocking"/> false memasang hook yang tidak menahan apa
    /// pun dan hanya menunggu kombinasi keluar. Bentuk ini dibutuhkan karena
    /// jalan keluar tidak boleh bergantung pada mode pemblokiran: versi pertama
    /// mendeteksi kombinasi di dalam pemblokir, sehingga saat pemblokiran
    /// dimatikan di mesin pengembangan, satu-satunya cara mengakhiri sesi dari
    /// sisi mahasiswa ikut hilang tanpa disadari.
    /// </summary>
    public void Install(bool blocking = true)
    {
        if (Active) return;
        Blocking = blocking;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);

        // Hook mouse hanya perlu bila memang menahan. Saat mengintai, ia tidak
        // punya pekerjaan dan hanya menambah beban pada setiap gerakan mouse.
        if (blocking) _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
    }

    public void Uninstall()
    {
        // Keadaan pengubah disetel ulang. Tombol yang tertekan saat sesi
        // berakhir tidak akan pernah mengirim peristiwa lepas ke hook ini,
        // sehingga sesi berikutnya akan mengira Ctrl masih ditahan.
        _ctrlDown = _altDown = _shiftDown = false;
        Blocking = false;

        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Suntikan kita sendiri harus lolos, kalau tidak seluruh fitur mati.
        if (info.dwExtraInfo == InputInjector.Signature)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var vk = (int)info.vkCode;
        var down = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;

        // Dicatat sebelum ditahan. Setelah return 1, peristiwa ini tidak ada
        // lagi bagi seluruh sistem termasuk bagi kita.
        switch (vk)
        {
            case VK_CONTROL or VK_LCONTROL or VK_RCONTROL: _ctrlDown = down; break;
            case VK_MENU or VK_LMENU or VK_RMENU: _altDown = down; break;
            case VK_SHIFT or VK_LSHIFT or VK_RSHIFT: _shiftDown = down; break;
        }

        // Jalan keluar mahasiswa. Kendali yang tidak dapat dihentikan dari sisi
        // yang dikendalikan bukan alat bantu, melainkan alat pengambilalihan.
        if (down && vk == VK_S && _ctrlDown && _altDown && _shiftDown)
        {
            EscapeRequested?.Invoke();
            return 1;
        }

        return Blocking
            ? 1                                                   // ditahan
            : CallNextHookEx(_keyboardHook, nCode, wParam, lParam);   // hanya mengintai
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

        if (info.dwExtraInfo == InputInjector.Signature)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        return Blocking
            ? 1
            : CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
