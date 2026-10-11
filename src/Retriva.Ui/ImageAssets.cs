using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Retriva.Ui;

/// <summary>
/// Memuat gambar aset dengan penyusutan yang benar.
///
/// MASALAHNYA HALUS DAN MUDAH SALAH DIDUGA.
///
/// Logo disimpan pada 1000x1000 — ukuran sumber yang memang tepat — lalu
/// ditampilkan pada 18 sampai 54 piksel. Bila diserahkan begitu saja ke WPF,
/// gambar didekode utuh pada 1000 piksel lalu disusutkan hampir dua puluh kali
/// memakai penyaringan bawaan yang murah. Hasilnya lembek, dan orang yang
/// melihatnya akan menyimpulkan logonya yang bermasalah — padahal berkasnya
/// tajam sempurna.
///
/// Dua hal memperbaikinya sekaligus:
///
///   DecodePixelWidth membuat dekoder JPEG/PNG menghasilkan gambar yang sudah
///   berukuran mendekati tujuan, memakai penyusutan berkualitas tinggi di dalam
///   dekoder itu sendiri. Ia juga jauh lebih hemat memori: 18 piksel alih-alih
///   sejuta piksel yang langsung dibuang.
///
///   BitmapScalingMode.HighQuality memakai penyaring Fant untuk sisa penyesuaian
///   terakhir, bukan interpolasi linear yang menjadi bawaan.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageAssets
{
    /// <summary>
    /// Kelipatan dekode terhadap ukuran tampil.
    ///
    /// Dua kali, bukan satu, karena penskalaan tampilan Windows biasa disetel
    /// 125 sampai 200 persen di lab. Mendekode tepat sebesar ukuran DIP akan
    /// menghasilkan gambar yang justru diperbesar lagi oleh Windows, dan blur
    /// yang sama muncul kembali lewat jalan lain.
    /// </summary>
    private const int DecodeScale = 2;

    /// <summary>
    /// Membuat Image siap pakai, atau null bila berkasnya tidak ada atau rusak.
    /// Pemanggil yang menentukan apa yang dipakai sebagai gantinya — ia yang
    /// tahu apakah penanda sementara pantas atau justru harus gagal terang.
    /// </summary>
    public static Image? TryLoad(string path, double displaySize)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = (int)Math.Ceiling(displaySize * DecodeScale);
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new Image
            {
                Source = bitmap,
                Width = displaySize,
                Height = displaySize,
                Stretch = Stretch.Uniform,
            };

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch (Exception ex) when (ex is IOException
                                     or NotSupportedException
                                     or UriFormatException
                                     or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Ikon jendela, didekode pada 256 piksel agar tajam di taskbar besar.</summary>
    public static BitmapImage? TryLoadIcon(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException
                                     or NotSupportedException
                                     or UriFormatException)
        {
            return null;
        }
    }
}
