using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Retriva.Media;

/// <summary>
/// Menyembunyikan jendela dari tangkapan layar, seperti yang dilakukan OBS
/// terhadap jendelanya sendiri.
///
/// Jendela tetap terlihat di monitor fisik, tetapi tidak ikut tertangkap.
/// Tanpa ini, dosen yang menyiarkan layar akan menyiarkan panel Retriva-nya
/// juga — termasuk daftar unit, kode pendaftaran, dan jendela penonton yang
/// menampilkan siarannya sendiri, yang menghasilkan efek cermin tak berujung.
///
/// Tersedia sejak Windows 10 versi 2004. Pada versi lebih lama panggilan ini
/// gagal dan jendela ikut tertangkap — perilaku lama, bukan kerusakan.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CaptureExclusion
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>
    /// Dapat dimatikan lewat RETRIVA_CAPTURE_EXCLUSION=off bila ternyata
    /// bermasalah pada kombinasi GPU tertentu. Diagnostik ini penting: bila
    /// jendela ikut menghilang dari monitor, penyebabnya harus bisa dikenali
    /// dan dimatikan tanpa membangun ulang aplikasi.
    /// </summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("RETRIVA_CAPTURE_EXCLUSION") != "off";

    /// <summary>Mengembalikan pesan diagnostik, bukan sekadar berhasil/gagal.</summary>
    public static string Apply(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "handle jendela belum ada";
        if (!Enabled) return "pengecualian capture dimatikan lewat variabel lingkungan";

        try
        {
            return SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE)
                ? "jendela dikecualikan dari tangkapan layar"
                : $"pengecualian gagal (error {Marshal.GetLastWin32Error()})";
        }
        catch (EntryPointNotFoundException)
        {
            return "Windows terlalu lama untuk WDA_EXCLUDEFROMCAPTURE";
        }
    }

    public static string Clear(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "handle jendela belum ada";
        try
        {
            return SetWindowDisplayAffinity(handle, WDA_NONE)
                ? "pengecualian dilepas"
                : $"gagal melepas ({Marshal.GetLastWin32Error()})";
        }
        catch (EntryPointNotFoundException) { return "tidak didukung"; }
    }
}
