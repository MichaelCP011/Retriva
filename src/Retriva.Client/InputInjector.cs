using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Retriva.Shared.Local;

namespace Retriva.Client;

/// <summary>
/// Menyuntikkan mouse dan keyboard ke sesi pengguna lewat SendInput.
///
/// Harus dipanggil dari proses di sesi interaktif. Layanan di Session 0 boleh
/// memanggil SendInput tanpa error, tetapi peristiwanya tidak sampai ke desktop
/// mana pun — jadi tampak berhasil sementara tidak terjadi apa-apa. Karena itu
/// injeksi tinggal di klien, bukan di agent.
///
/// BATAS YANG TIDAK DAPAT DILANGGAR:
///
/// UIPI menolak input dari proses berintegritas lebih rendah ke jendela
/// berintegritas lebih tinggi. Klien berjalan sebagai pengguna biasa, jadi
/// jendela yang dijalankan sebagai administrator tidak dapat dikendalikan —
/// SendInput mengembalikan jumlah yang benar dan tetap tidak berpengaruh.
/// Itu sebabnya nilai kembaliannya diperiksa dan dilaporkan: satu-satunya
/// gejala lain hanyalah "klik saya tidak melakukan apa pun".
///
/// Desktop aman — Ctrl+Alt+Del, prompt UAC, layar login — mustahil dijangkau
/// dari mode pengguna, dan itu memang dirancang begitu oleh Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputInjector
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    /// <summary>
    /// Penanda pada dwExtraInfo agar peristiwa buatan kita dapat dikenali oleh
    /// hook pemblokir milik kita sendiri.
    ///
    /// Tanpa penanda ini pemblokiran input mahasiswa juga akan memblokir
    /// suntikan admin: hook tingkat rendah melihat peristiwa SendInput sama
    /// seperti peristiwa dari perangkat fisik. Akibatnya kendali jarak jauh
    /// mati total sementara semua log menyatakan perintah terkirim dan diterima.
    /// </summary>
    public static readonly IntPtr Signature = new(0x52545256);   // "RTRV"

    /// <summary>Alasan kegagalan terakhir, atau null bila semua suntikan diterima.</summary>
    public string? LastFault { get; private set; }

    public void Apply(InjectInput message)
    {
        var input = message.Kind switch
        {
            RemoteInputKind.MouseMove => MouseAt(message, MOUSEEVENTF_MOVE),
            RemoteInputKind.MouseDown => MouseAt(message, ButtonFlag(message.Button, down: true)),
            RemoteInputKind.MouseUp => MouseAt(message, ButtonFlag(message.Button, down: false)),
            RemoteInputKind.MouseWheel => Wheel(message),
            RemoteInputKind.KeyDown => Key(message.KeyCode, up: false),
            RemoteInputKind.KeyUp => Key(message.KeyCode, up: true),
            _ => (INPUT?)null,
        };

        if (input is null) return;

        var array = new[] { input.Value };
        var sent = SendInput(1, array, Marshal.SizeOf<INPUT>());

        // SendInput mengembalikan jumlah peristiwa yang berhasil dimasukkan ke
        // aliran input. Nol berarti diblokir — hampir selalu UIPI, kadang hook
        // aplikasi lain. Perbedaan itu penting bagi orang yang sedang mencoba
        // memahami mengapa klik tidak berpengaruh.
        LastFault = sent == 0
            ? $"suntikan {message.Kind} ditolak (error {Marshal.GetLastWin32Error()}) — "
            + "kemungkinan jendela berjalan sebagai administrator"
            : null;
    }

    /// <summary>
    /// Mengubah koordinat per-sepuluh-ribu menjadi koordinat absolut SendInput.
    ///
    /// SendInput memakai skala 0..65535 atas seluruh desktop virtual, bukan
    /// piksel. Pemetaannya lewat layar utama dulu, karena pecahan yang dikirim
    /// admin mengacu pada tangkapan layar utama, sedangkan desktop virtual bisa
    /// jauh lebih lebar bila ada monitor kedua.
    /// </summary>
    private static INPUT MouseAt(InjectInput message, uint flags)
    {
        var primaryWidth = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        var primaryHeight = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));

        var pixelX = message.X / 10000.0 * primaryWidth;
        var pixelY = message.Y / 10000.0 * primaryHeight;

        var virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var virtualWidth = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var virtualHeight = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var absoluteX = (int)Math.Round((pixelX - virtualLeft) * 65535 / virtualWidth);
        var absoluteY = (int)Math.Round((pixelY - virtualTop) * 65535 / virtualHeight);

        return new INPUT
        {
            type = INPUT_MOUSE,
            union = new INPUTUNION
            {
                mouse = new MOUSEINPUT
                {
                    dx = Math.Clamp(absoluteX, 0, 65535),
                    dy = Math.Clamp(absoluteY, 0, 65535),
                    dwFlags = flags | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = Signature,
                },
            },
        };
    }

    private static INPUT Wheel(InjectInput message) => new()
    {
        type = INPUT_MOUSE,
        union = new INPUTUNION
        {
            mouse = new MOUSEINPUT
            {
                mouseData = message.WheelDelta,
                dwFlags = MOUSEEVENTF_WHEEL,
                dwExtraInfo = Signature,
            },
        },
    };

    private static INPUT Key(int keyCode, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;

        // Tombol panah, Home/End, Insert/Delete, dan Ctrl/Alt kanan harus
        // ditandai extended. Tanpa penanda itu Windows menafsirkannya sebagai
        // tombol papan numerik, sehingga panah bergerak salah arah atau tidak
        // berpengaruh sama sekali.
        if (IsExtended(keyCode)) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUTUNION
            {
                keyboard = new KEYBDINPUT
                {
                    wVk = (ushort)keyCode,
                    dwFlags = flags,
                    dwExtraInfo = Signature,
                },
            },
        };
    }

    private static bool IsExtended(int vk) => vk is
        0x21 or 0x22 or         // PageUp, PageDown
        0x23 or 0x24 or         // End, Home
        0x25 or 0x26 or 0x27 or 0x28 or  // panah
        0x2D or 0x2E or         // Insert, Delete
        0x90 or                 // NumLock
        0xA3 or 0xA5;           // Ctrl kanan, Alt kanan

    private static uint ButtonFlag(int button, bool down) => button switch
    {
        1 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    // Union diwakili dengan FieldOffset agar tidak memerlukan blok unsafe.
    // Ukurannya harus cocok dengan struct asli Windows, kalau tidak SendInput
    // menolak seluruh larik dengan ERROR_INVALID_PARAMETER.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
