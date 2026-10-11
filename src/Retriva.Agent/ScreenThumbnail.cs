using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Retriva.Agent;

/// <summary>
/// Pratinjau layar beresolusi rendah untuk grid dashboard.
///
/// Sengaja memakai GDI, bukan Windows.Graphics.Capture. Untuk gambar 240x135
/// setiap dua detik, GDI sudah memadai dan jauh lebih sederhana. WGC baru
/// dipakai di M4 untuk siaran layar, yang memang butuh 30 fps dan encoder keras.
///
/// CATATAN PENTING: saat agent berjalan sebagai Windows Service (Session 0),
/// pengambilan gambar ini akan menghasilkan layar hitam karena isolasi sesi.
/// Solusinya adalah proses pembantu di sesi pengguna — lihat catatan di README.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenThumbnail
{
    /// <summary>
    /// Lebar bawaan pratinjau.
    ///
    /// Dinaikkan dari 240 setelah terbukti tidak terpakai: pada ukuran itu
    /// layar 1920 menyusut delapan kali dan tidak ada satu pun teks yang
    /// terbaca, sehingga pratinjaunya hanya membuktikan PC menyala — bukan
    /// menunjukkan apa yang sedang dikerjakan.
    ///
    /// Ongkosnya kecil: 480x270 pada kualitas 65 sekitar 25 KB, dan 40 unit
    /// setiap 2 detik berarti sekitar 4 Mbps di jaringan gigabit. Biaya
    /// tangkapnya sendiri tidak berubah, karena yang mahal adalah membaca
    /// layar, bukan mengecilkannya.
    /// </summary>
    public const int DefaultWidth = 480;

    /// <summary>Ukuran yang dipakai saat ini, ditentukan konsol.</summary>
    public static int Width { get; private set; } = DefaultWidth;
    public static int Height { get; private set; } = DefaultWidth * 9 / 16;

    /// <summary>
    /// Mengatur ukuran pratinjau dari konsol.
    ///
    /// Dibatasi agar satu konsol yang salah setel tidak dapat menyuruh 40 agent
    /// mengirim gambar layar penuh setiap dua detik.
    /// </summary>
    public static void Resize(int width)
    {
        Width = Math.Clamp(width, 240, 960);
        Height = Math.Max(1, Width * 9 / 16);
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // DllImport, bukan LibraryImport: signature ini hanya berisi tipe blittable
    // sehingga tidak ada marshalling yang perlu digenerate, dan kita terhindar
    // dari keharusan menyalakan AllowUnsafeBlocks di seluruh project.
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static bool _warned;

    /// <summary>Mengembalikan JPEG kecil, atau array kosong bila layar tidak terjangkau.</summary>
    public static byte[] Capture(ILogger? log = null)
    {
        try
        {
            var screenWidth = GetSystemMetrics(SM_CXSCREEN);
            var screenHeight = GetSystemMetrics(SM_CYSCREEN);
            if (screenWidth <= 0 || screenHeight <= 0) return [];

            using var full = new Bitmap(screenWidth, screenHeight);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight));
            }

            using var thumb = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, Width, Height);
            }

            return Encode(thumb, quality: 65);
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                log?.LogWarning(
                    "Pratinjau layar tidak dapat diambil ({Message}). "
                  + "Bila agent berjalan sebagai Windows Service, ini wajar karena isolasi Session 0.",
                    ex.Message);
            }
            return [];
        }
    }

    private static byte[] Encode(Bitmap bitmap, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        using var stream = new MemoryStream();
        if (encoder is null)
        {
            bitmap.Save(stream, ImageFormat.Jpeg);
            return stream.ToArray();
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(stream, encoder, parameters);
        return stream.ToArray();
    }
}
