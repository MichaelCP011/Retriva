using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Retriva.Client;

/// <summary>
/// Memblokir pintasan keluar selama layar terkunci: Alt+Tab, tombol Windows,
/// Ctrl+Esc, Alt+Esc, dan Alt+F4.
///
/// BATAS YANG JUJUR: Ctrl+Alt+Del tidak dapat diblokir dari mode pengguna.
/// Windows menanganinya di secure desktop, dan itu memang disengaja — kalau
/// bisa ditembus aplikasi biasa, setiap malware akan memakainya untuk
/// memalsukan layar login. Konsekuensinya mahasiswa tetap dapat membuka
/// Task Manager dan menutup RetrivaClient. Yang bisa dilakukan bukan
/// mencegahnya, melainkan mendeteksinya: agent melaporkan ke konsol bila
/// klien menghilang saat kunci sedang aktif.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_F4 = 0x73;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11;

    private const uint LLKHF_ALTDOWN = 0x20;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Delegat WAJIB disimpan di field. Bila hanya dilewatkan sebagai argumen,
    // GC akan mengumpulkannya sementara Windows masih memegang pointernya,
    // dan aplikasi crash beberapa saat kemudian di tempat yang tidak berhubungan.
    private readonly HookProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    public KeyboardBlocker() => _proc = OnKey;

    public bool Active => _hook != IntPtr.Zero;

    public void Install()
    {
        if (Active) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (!Active) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (ShouldBlock(info))
                {
                    // Mengembalikan 1 menghentikan penerusan, jadi Windows
                    // tidak pernah melihat penekanan tombol itu.
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(KBDLLHOOKSTRUCT info)
    {
        var vk = (int)info.vkCode;
        var altDown = (info.flags & LLKHF_ALTDOWN) != 0;
        var ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        return vk switch
        {
            VK_LWIN or VK_RWIN => true,                 // tombol Windows
            VK_TAB when altDown => true,                // Alt+Tab
            VK_ESCAPE when altDown || ctrlDown => true,  // Alt+Esc, Ctrl+Esc
            VK_F4 when altDown => true,                 // Alt+F4
            _ => false,
        };
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
