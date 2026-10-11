using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Retriva.Media;

/// <summary>Satu frame beserta biaya pembuatannya, untuk pengukuran maupun pengiriman.</summary>
public sealed record CapturedFrame(
    byte[] Data, int Width, int Height, double CaptureMs, double ScaleMs, double EncodeMs)
{
    public double TotalMs => CaptureMs + ScaleMs + EncodeMs;
}

public interface IScreenSource : IDisposable
{
    /// <summary>Mengambil satu frame, atau null bila layar tidak terjangkau.</summary>
    CapturedFrame? Grab();
}

/// <summary>
/// Sumber frame berbasis GDI dengan hasil JPEG.
///
/// Memakai Motion-JPEG lebih dulu, bukan H.264 lewat Media Foundation.
/// Alasannya urutan risiko: jalur capture dan encode JPEG sudah terbukti
/// bekerja, sehingga transport dapat dibangun dan diuji di atas fondasi
/// yang pasti. Encoder ditukar belakangan di balik antarmuka ini, tanpa
/// menyentuh transport maupun pemutar di sisi penerima.
///
/// Ongkosnya nyata: setiap frame berdiri sendiri tanpa kompresi antar-frame,
/// jadi bandwidth sekitar dua sampai tiga kali H.264. Imbalannya, satu paket
/// hilang hanya menjatuhkan satu frame — tidak merusak beruntun seperti
/// H.264 yang kehilangan keyframe.
/// </summary>
/// <summary>Keadaan jendela yang sedang disiarkan.</summary>
public enum TargetHealth
{
    Healthy,
    /// <summary>Jendela ditutup — siaran tidak dapat dilanjutkan.</summary>
    Closed,
    /// <summary>Diminimalkan: tidak ada piksel, tetapi bisa dibuka lagi.</summary>
    Minimized,
    /// <summary>Ukurannya berubah — bitmap perlu dibuat ulang.</summary>
    Resized,
}

[SupportedOSPlatform("windows")]
public sealed class GdiScreenSource : IScreenSource
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    // Meminta DWM menyerahkan permukaan gambar jendela, bukan menggambar ulang
    // isinya lewat WM_PRINT. Bendera inilah yang membuat jendela berakselerasi
    // GPU ikut terbaca; tanpanya banyak aplikasi modern menghasilkan bidang hitam.
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    private readonly CaptureTarget _target;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    /// <summary>
    /// Kualitas JPEG, DAPAT DIUBAH SAAT SIARAN BERJALAN.
    ///
    /// Bukan readonly, dan itu disengaja. Ukuran frame JPEG bergantung pada isi
    /// layar, bukan pada resolusinya: layar polos 80 KB, layar penuh gambar
    /// bergerak 450 KB. Selisihnya lima kali lipat, dan pada 15 fps itu selisih
    /// antara 10 Mbps dan 53 Mbps — antara berjalan lancar dan seluruh kelas
    /// tersendat.
    ///
    /// Menurunkan kualitas adalah satu-satunya cara menahan laju tanpa
    /// mengganti resolusi. Mengganti resolusi di tengah siaran memaksa penonton
    /// menskalakan ulang dan terlihat jauh lebih buruk daripada gambar yang
    /// sedikit lebih lembut.
    /// </summary>
    private long _quality;

    /// <summary>
    /// Menyetel kualitas JPEG. Berlaku pada frame berikutnya.
    ///
    /// Parameter encoder DIBANGUN ULANG, tidak cukup mengubah _quality saja.
    /// EncoderParameter menyalin nilainya ke memori tak terkelola saat dibuat,
    /// jadi mengubah medan di sisi terkelola tidak berpengaruh apa pun —
    /// kualitasnya akan tampak berubah di log dan tetap sama di gambarnya.
    ///
    /// Hanya boleh dipanggil dari thread yang sama dengan Grab. Pemanggilnya
    /// satu-satunya adalah loop tangkapan itu sendiri, jadi tidak ada kunci di
    /// sini — kunci yang dipasang untuk pemanggil yang tidak ada hanya
    /// menambah biaya pada jalur terpanas di aplikasi ini.
    /// </summary>
    public void SetQuality(int quality)
    {
        var clamped = Math.Clamp(quality, 30, 98);
        if (clamped == _quality) return;

        _quality = clamped;
        _encoderParams.Param[0]?.Dispose();
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, _quality);
    }

    public int Quality => (int)_quality;
    private readonly ImageCodecInfo? _jpegCodec;

    // Bitmap dan Graphics dipakai ulang antar frame. Mengalokasikannya setiap
    // frame pada 10-30 fps akan membebani GC jauh lebih berat daripada
    // pekerjaan capture-nya sendiri.
    private readonly Bitmap _full;
    private readonly Bitmap _scaled;
    private readonly Graphics _fullGraphics;
    private readonly Graphics _scaledGraphics;
    private readonly EncoderParameters _encoderParams;
    private readonly MemoryStream _buffer = new(256 * 1024);

    // Persegi sumber dan tujuan dihitung sekali, bukan tiap frame.
    private readonly Rectangle _sourceRect;
    private readonly Rectangle _destination;

    public int ScreenWidth { get; }
    public int ScreenHeight { get; }
    public int SourceX { get; }
    public int SourceY { get; }
    public string TargetLabel => _target.Label;

    /// <summary>Persegi yang benar-benar ditangkap — untuk diagnosis, bukan tebakan.</summary>
    public string CaptureRect => $"{ScreenWidth}x{ScreenHeight} di ({SourceX},{SourceY})";


    /// <summary>True bila sumbernya satu jendela, bukan area layar.</summary>
    private bool CaptureByWindow => _target.Kind == CaptureKind.Window
                                 && _target.WindowHandle != IntPtr.Zero;

    /// <summary>Cara tangkap yang benar-benar dipakai — untuk diagnosis.</summary>
    public string CaptureMethod { get; private set; } = "layar";

    public GdiScreenSource(int targetWidth = 1280, int quality = 55, CaptureTarget? target = null)
    {
        // Persegi dibaca ulang di sini, bukan dipakai apa adanya dari daftar
        // sumber. Daftar itu bisa sudah berumur beberapa menit.
        _target = CaptureTargets.Refresh(target ?? CaptureTarget.PrimaryScreen());

        if (_target.Kind == CaptureKind.FullScreen || _target.Width <= 0)
        {
            SourceX = 0;
            SourceY = 0;
            ScreenWidth = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
            ScreenHeight = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        }
        else
        {
            SourceX = _target.X;
            SourceY = _target.Y;
            ScreenWidth = Math.Max(1, _target.Width);
            ScreenHeight = Math.Max(1, _target.Height);
        }

        _targetWidth = Math.Min(targetWidth, ScreenWidth);
        _targetHeight = Math.Max(1, (int)Math.Round(_targetWidth * (double)ScreenHeight / ScreenWidth));
        _quality = Math.Clamp(quality, 1, 100);

        _full = new Bitmap(ScreenWidth, ScreenHeight, PixelFormat.Format32bppArgb);
        _scaled = new Bitmap(_targetWidth, _targetHeight, PixelFormat.Format32bppArgb);
        _fullGraphics = Graphics.FromImage(_full);
        _scaledGraphics = Graphics.FromImage(_scaled);

        // Pengukuran di i5-9400 menunjukkan penskalaan memakan 13 ms per frame
        // dengan HighQualityBilinear — sepertiga dari seluruh anggaran waktu,
        // untuk perbedaan yang nyaris tak terlihat pada teks dan slide.
        //
        // Bilinear biasa jauh lebih murah. SourceCopy melewati pencampuran alpha
        // yang tidak diperlukan karena sumbernya buram sepenuhnya.
        _scaledGraphics.InterpolationMode = InterpolationMode.Bilinear;
        _scaledGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        _scaledGraphics.SmoothingMode = SmoothingMode.None;
        _scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        _scaledGraphics.CompositingMode = CompositingMode.SourceCopy;

        _sourceRect = new Rectangle(0, 0, ScreenWidth, ScreenHeight);
        _destination = new Rectangle(0, 0, _targetWidth, _targetHeight);

        _jpegCodec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encoderParams = new EncoderParameters(1);
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, _quality);
    }

    /// <summary>
    /// Keadaan sumber tangkapan, diperiksa berkala selama siaran berjalan.
    ///
    /// Tanpa pemeriksaan ini, jendela yang ditutup di tengah siaran membuat
    /// PrintWindow gagal pada handle mati lalu jatuh ke penyalinan layar dengan
    /// koordinat lama — hasilnya potongan layar acak yang disiarkan tanpa satu
    /// pun pesan kesalahan. Kegagalan yang tidak bersuara seperti itu jauh
    /// lebih mahal daripada siaran yang berhenti dengan alasan jelas.
    /// </summary>
    public TargetHealth CheckTarget()
    {
        if (!CaptureByWindow) return TargetHealth.Healthy;
        if (!IsWindow(_target.WindowHandle)) return TargetHealth.Closed;
        if (CaptureTargets.IsMinimized(_target.WindowHandle)) return TargetHealth.Minimized;

        var fresh = CaptureTargets.Refresh(_target);
        return fresh.Width != ScreenWidth || fresh.Height != ScreenHeight
            ? TargetHealth.Resized
            : TargetHealth.Healthy;
    }

    public CapturedFrame? Grab()
    {
        try
        {
            // Tiga tahap diukur terpisah. Menggabungkannya menyembunyikan
            // tahap mana yang sebenarnya mahal, dan optimasi jadi menebak.
            var watch = Stopwatch.StartNew();

            // Dua cara tangkap yang berbeda secara mendasar, bukan dua varian
            // dari hal yang sama.
            //
            // CopyFromScreen menyalin piksel LAYAR pada koordinat tertentu. Untuk
            // seluruh layar dan monitor itu tepat. Untuk satu jendela itu salah:
            // yang tersalin adalah apa pun yang sedang tampil di posisi jendela
            // tersebut, jadi jendela yang tertimpa aplikasi lain akan menyiarkan
            // aplikasi lain itu.
            //
            // PrintWindow meminta jendelanya sendiri menyerahkan gambarnya,
            // terlepas dari urutan tumpukan. Itulah yang membuat "siarkan Word"
            // tetap menyiarkan Word ketika pengguna berpindah ke editor lain.
            if (CaptureByWindow) GrabWindow();
            else _fullGraphics.CopyFromScreen(SourceX, SourceY, 0, 0, new Size(ScreenWidth, ScreenHeight));

            var captureMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            _scaledGraphics.DrawImage(_full, _destination, _sourceRect, GraphicsUnit.Pixel);
            var scaleMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            _buffer.SetLength(0);
            if (_jpegCodec is null) _scaled.Save(_buffer, ImageFormat.Jpeg);
            else _scaled.Save(_buffer, _jpegCodec, _encoderParams);
            var encodeMs = watch.Elapsed.TotalMilliseconds;

            return new CapturedFrame(_buffer.ToArray(), _targetWidth, _targetHeight,
                captureMs, scaleMs, encodeMs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // Terjadi bila layar sedang tidak terjangkau, mis. saat transisi
            // sesi atau ketika proses berada di Session 0.
            return null;
        }
    }

    /// <summary>
    /// Menangkap satu jendela lewat DWM, dengan jatuh kembali ke penyalinan
    /// layar bila jendelanya menolak.
    ///
    /// Sebagian aplikasi — terutama yang menggambar lewat overlay perangkat
    /// keras — mengembalikan bidang hitam dari PrintWindow. Kegagalan itu tidak
    /// selalu dilaporkan lewat nilai kembalian, jadi hasilnya diperiksa: frame
    /// yang seluruhnya hitam dianggap gagal dan diambil ulang dari layar.
    /// Menyiarkan jendela yang tertimpa masih lebih berguna daripada kotak hitam.
    /// </summary>
    private void GrabWindow()
    {
        var handle = _target.WindowHandle;
        var printed = false;

        var hdc = _fullGraphics.GetHdc();
        try { printed = PrintWindow(handle, hdc, PW_RENDERFULLCONTENT); }
        finally { _fullGraphics.ReleaseHdc(hdc); }

        if (printed && !LooksBlank())
        {
            CaptureMethod = "jendela (PrintWindow)";
            return;
        }

        CaptureMethod = printed ? "layar (PrintWindow kosong)" : "layar (PrintWindow gagal)";
        _fullGraphics.CopyFromScreen(SourceX, SourceY, 0, 0, new Size(ScreenWidth, ScreenHeight));
    }

    /// <summary>
    /// Memeriksa beberapa titik sebar, bukan seluruh piksel.
    ///
    /// Membaca 2 juta piksel tiap frame untuk mendeteksi bidang hitam akan
    /// memakan lebih banyak waktu daripada tangkapannya sendiri. Sembilan
    /// titik cukup: jendela sungguhan hampir tidak mungkin hitam pekat di
    /// kesembilan titik itu sekaligus.
    /// </summary>
    private bool LooksBlank()
    {
        for (var ix = 1; ix <= 3; ix++)
        {
            for (var iy = 1; iy <= 3; iy++)
            {
                var x = Math.Min(ScreenWidth - 1, ScreenWidth * ix / 4);
                var y = Math.Min(ScreenHeight - 1, ScreenHeight * iy / 4);
                var pixel = _full.GetPixel(x, y);
                if (pixel.R > 8 || pixel.G > 8 || pixel.B > 8) return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        _encoderParams.Dispose();
        _scaledGraphics.Dispose();
        _fullGraphics.Dispose();
        _scaled.Dispose();
        _full.Dispose();
        _buffer.Dispose();
    }
}
