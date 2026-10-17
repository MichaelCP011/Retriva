using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Retriva.Media;

/// <summary>
/// Pratinjau layar beresolusi rendah untuk grid dashboard.
///
/// Sengaja memakai GDI, bukan Windows.Graphics.Capture. Untuk gambar 480x270
/// setiap dua detik, GDI sangat ringan, efisien, dan andal di sesi desktop pengguna.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenThumbnail
{
    /// <summary>
    /// Lebar bawaan pratinjau.
    ///
    /// 480x270 pada kualitas 65 sekitar 25 KB, dan 40 unit
    /// setiap 2 detik berarti sekitar 4 Mbps di jaringan gigabit.
    /// </summary>
    public const int DefaultWidth = 480;

    /// <summary>Ukuran yang dipakai saat ini, ditentukan konsol.</summary>
    public static int Width { get; private set; } = DefaultWidth;
    public static int Height { get; private set; } = DefaultWidth * 9 / 16;

    /// <summary>
    /// Mengatur ukuran pratinjau dari konsol.
    /// </summary>
    public static void Resize(int width)
    {
        Width = Math.Clamp(width, 240, 960);
        Height = Math.Max(1, Width * 9 / 16);
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

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
