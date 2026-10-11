using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Retriva.Media;

/// <summary>Apa yang hendak ditangkap.</summary>
public enum CaptureKind { FullScreen, Monitor, Window }

public sealed record CaptureTarget(
    CaptureKind Kind, string Label, IntPtr WindowHandle = default,
    int X = 0, int Y = 0, int Width = 0, int Height = 0)
{
    public static CaptureTarget PrimaryScreen() =>
        new(CaptureKind.FullScreen, "Seluruh layar utama");
}

/// <summary>
/// Mendaftar layar dan jendela yang dapat disiarkan.
///
/// Yang disaring hanya jendela puncak yang terlihat dan berjudul. Tanpa
/// penyaringan, daftarnya dipenuhi jendela tersembunyi milik sistem yang
/// tidak berarti apa-apa bagi dosen yang sedang memilih apa yang mau dibagikan.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CaptureTargets
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool EnumMonitorsProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumMonitorsProc proc, IntPtr data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out bool value, int size);

    private const int DWMWA_CLOAKED = 14;

    public static List<CaptureTarget> Monitors()
    {
        var result = new List<CaptureTarget> { CaptureTarget.PrimaryScreen() };
        var index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT rect, IntPtr _) =>
        {
            index++;
            result.Add(new CaptureTarget(
                CaptureKind.Monitor, $"Monitor {index} ({rect.Right - rect.Left}x{rect.Bottom - rect.Top})",
                X: rect.Left, Y: rect.Top,
                Width: rect.Right - rect.Left, Height: rect.Bottom - rect.Top));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Jendela yang diminimalkan tidak punya piksel untuk ditangkap.</summary>
    public static bool IsMinimized(IntPtr handle) => IsIconic(handle);

    /// <summary>
    /// Membaca ulang persegi jendela saat siaran dimulai.
    ///
    /// Persegi dalam daftar sumber direkam ketika daftarnya dibuat. Jendela
    /// bisa dipindah atau diubah ukurannya setelah itu, dan menangkap dengan
    /// angka lama menghasilkan area yang salah — persis gejala jendela 118x200
    /// yang muncul saat menyiarkan aplikasi yang sudah berubah ukuran.
    /// </summary>
    public static CaptureTarget Refresh(CaptureTarget target)
    {
        if (target.Kind != CaptureKind.Window) return target;
        if (target.WindowHandle == IntPtr.Zero) return target;
        if (!GetWindowRect(target.WindowHandle, out var rect)) return target;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return target;

        return target with
        {
            X = rect.Left, Y = rect.Top, Width = width, Height = height,
        };
    }

    public static List<CaptureTarget> Windows()
    {
        var result = new List<CaptureTarget>();
        var shell = GetShellWindow();

        EnumWindows((handle, _) =>
        {
            if (handle == shell || !IsWindowVisible(handle)) return true;

            var length = GetWindowTextLength(handle);
            if (length == 0) return true;

            // Jendela UWP yang sudah ditutup tetap terdaftar sebagai "visible"
            // tetapi berstatus cloaked. Tanpa saringan ini daftarnya penuh
            // aplikasi hantu yang tidak terlihat di layar.
            if (DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked)
                return true;

            if (!GetWindowRect(handle, out var rect)) return true;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 160 || height < 120) return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);

            result.Add(new CaptureTarget(
                CaptureKind.Window, title.ToString(), handle,
                rect.Left, rect.Top, width, height));
            return true;
        }, IntPtr.Zero);

        return result.OrderBy(w => w.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
