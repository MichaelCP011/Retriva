using System.Runtime.Versioning;

namespace Retriva.Media;

public sealed record BenchmarkResult(
    int Width, int Height, int Quality, int Frames,
    double AvgCaptureMs, double AvgScaleMs, double AvgEncodeMs, double P95TotalMs,
    double AvgKb, double MaxKb, bool ScreenWasStatic)
{
    /// <summary>Frame per detik tertinggi yang mungkin dari biaya capture dan encode saja.</summary>
    public double MaxFps => 1000.0 / Math.Max(0.1, AvgCaptureMs + AvgScaleMs + AvgEncodeMs);

    /// <summary>Bandwidth ke SATU penerima pada fps tertentu.</summary>
    public double MbpsPerClient(int fps) => AvgKb * 8 * fps / 1000.0;

    /// <summary>Bandwidth total bila dikirim unicast ke sejumlah unit.</summary>
    public double MbpsTotal(int fps, int clients) => MbpsPerClient(fps) * clients;
}

/// <summary>
/// Mengukur biaya capture dan encode sungguhan sebelum transport dibangun.
///
/// Angka ini yang menentukan resolusi dan fps yang realistis. Menebaknya lalu
/// membangun transport di atas tebakan itu berarti baru menemukan masalahnya
/// setelah semuanya jadi.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CaptureBenchmark
{
    public static BenchmarkResult Run(int targetWidth, int quality, int frames = 60)
    {
        using var source = new GdiScreenSource(targetWidth, quality);

        var captureMs = new List<double>(frames);
        var scaleMs = new List<double>(frames);
        var encodeMs = new List<double>(frames);
        var totalMs = new List<double>(frames);
        var sizesKb = new List<double>(frames);
        var distinctSizes = new HashSet<int>();

        // Beberapa frame pertama dibuang: alokasi awal dan pemanasan JIT
        // membuatnya jauh lebih lambat dan mencemari rata-rata.
        for (var i = 0; i < 5; i++) source.Grab();

        for (var i = 0; i < frames; i++)
        {
            var frame = source.Grab();
            if (frame is null) continue;

            captureMs.Add(frame.CaptureMs);
            scaleMs.Add(frame.ScaleMs);
            encodeMs.Add(frame.EncodeMs);
            totalMs.Add(frame.TotalMs);
            sizesKb.Add(frame.Data.Length / 1024.0);
            distinctSizes.Add(frame.Data.Length);
        }

        if (totalMs.Count == 0)
            return new BenchmarkResult(0, 0, quality, 0, 0, 0, 0, 0, 0, 0, false);

        totalMs.Sort();
        var p95 = totalMs[Math.Min(totalMs.Count - 1, (int)(totalMs.Count * 0.95))];

        return new BenchmarkResult(
            source.ScreenWidth, source.ScreenHeight, quality, totalMs.Count,
            captureMs.Average(), scaleMs.Average(), encodeMs.Average(), p95,
            sizesKb.Average(), sizesKb.Max(),
            // Bila semua frame berukuran sama persis, layarnya diam sepanjang
            // pengukuran. Angka ukuran frame lalu mewakili kondisi terbaik,
            // bukan kondisi mengajar yang sesungguhnya.
            ScreenWasStatic: distinctSizes.Count <= 2);
    }

    /// <summary>Ringkasan siap tempel ke laporan.</summary>
    public static IEnumerable<string> Describe(BenchmarkResult r, int targetWidth)
    {
        if (r.Frames == 0)
        {
            yield return "Benchmark gagal: layar tidak dapat ditangkap.";
            yield break;
        }

        yield return $"Layar sumber   : {r.Width}x{r.Height}";
        yield return $"Target siaran  : lebar {targetWidth}px · kualitas {r.Quality}";
        yield return $"Capture layar  : {r.AvgCaptureMs:F1} ms/frame  (GDI CopyFromScreen)";
        yield return $"Penskalaan     : {r.AvgScaleMs:F1} ms/frame";
        yield return $"Encode JPEG    : {r.AvgEncodeMs:F1} ms/frame";
        yield return $"Total p95      : {r.P95TotalMs:F1} ms/frame";
        yield return $"Ukuran frame   : {r.AvgKb:F0} KB rata-rata · {r.MaxKb:F0} KB puncak";
        yield return $"Batas atas fps : {r.MaxFps:F0} fps (hanya dari capture+encode)";
        yield return "";
        yield return $"Bandwidth 10 fps: {r.MbpsPerClient(10):F1} Mbps/unit · "
                   + $"{r.MbpsTotal(10, 40):F0} Mbps untuk 40 unit";
        yield return $"Bandwidth 15 fps: {r.MbpsPerClient(15):F1} Mbps/unit · "
                   + $"{r.MbpsTotal(15, 40):F0} Mbps untuk 40 unit";
        yield return $"Bandwidth 30 fps: {r.MbpsPerClient(30):F1} Mbps/unit · "
                   + $"{r.MbpsTotal(30, 40):F0} Mbps untuk 40 unit";
        yield return "";
        yield return "Jaringan lab 1 Gbps. Di atas ~600 Mbps total, uplink switch";
        yield return "mulai jadi hambatan dan resolusi atau fps perlu diturunkan.";

        if (r.ScreenWasStatic)
        {
            yield return "";
            yield return "PERINGATAN: layar tidak berubah selama pengukuran, sehingga";
            yield return "ukuran frame di atas adalah kondisi terbaik. Konten bergerak";
            yield return "seperti video atau animasi bisa dua sampai empat kali lebih besar.";
        }
    }
}
