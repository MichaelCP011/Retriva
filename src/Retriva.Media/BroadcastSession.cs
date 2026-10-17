using System.Diagnostics;
using System.Net;
using Retriva.Shared.Broadcast;
using System.Runtime.Versioning;

namespace Retriva.Media;

public sealed record BroadcastSettings(
    int TargetWidth = 1920, int Quality = 80, int Fps = 15,
    int Port = FramePacket.DefaultPort,
    string MulticastGroup = FramePacket.MulticastGroup,
    CaptureTarget? Target = null,

    /// <summary>
    /// Bila diisi, frame dikirim unicast ke alamat ini alih-alih multicast.
    ///
    /// Dipakai kendali jarak jauh, yang arahnya berlawanan dengan siaran: satu
    /// mahasiswa mengirim layarnya ke satu admin. Multicast tidak masuk akal di
    /// sana — hanya ada satu penonton, dan mengirim ke grup berarti seluruh lab
    /// menerima layar mahasiswa itu.
    /// </summary>
    string? UnicastHost = null);

public sealed record BroadcastStatus(
    long FramesSent, long PacketsSent, long BytesSent, long SendErrors,
    double ActualFps, int TargetCount, bool Frozen);

/// <summary>
/// Loop siaran di sisi pengirim: tangkap, encode, kirim, ulangi.
///
/// Penjadwalannya ABSOLUT, bukan relatif per frame. Versi pertama menghitung
/// jeda sebagai "interval dikurangi waktu frame ini", dan hasilnya 10,8 fps
/// dari target 12 — karena Task.Delay di Windows bergranularitas sekitar
/// 15,6 ms, sehingga setiap frame kehilangan beberapa milidetik dan
/// kesalahannya menumpuk. Dengan tenggat absolut, keterlambatan satu frame
/// terkoreksi oleh frame berikutnya.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BroadcastSession : IAsyncDisposable
{
    private readonly BroadcastSettings _settings;
    private readonly FrameSender _sender = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private GdiScreenSource? _source;

    private long _framesSent;
    private readonly Stopwatch _uptime = new();

    private long _lastHealthCheck;
    private long _lastKeepAlive;
    private bool _frozenByMinimize;
    private CapturedFrame? _lastCaptured;

    /// <summary>
    /// Frame terakhir yang dikirim, agar konsol dapat menampilkan pratinjau
    /// dari apa yang benar-benar disiarkan — bukan kotak kosong yang tidak
    /// membedakan siaran berjalan dari siaran gagal.
    /// </summary>
    public byte[] LastFrame { get; private set; } = [];

    public bool Frozen { get; set; }

    /// <summary>Jendela target sedang diminimalkan, jadi gambarnya tertahan.</summary>
    public bool TargetMinimized => _frozenByMinimize;
    public bool Running => _loop is { IsCompleted: false };
    public string TargetLabel => _source?.TargetLabel ?? _settings.Target?.Label ?? "belum dipilih";
    public string CaptureRect => _source?.CaptureRect ?? "-";
    public string CaptureMethod => _source?.CaptureMethod ?? "-";

    /// <summary>
    /// Alasan siaran tidak dapat dilanjutkan, atau null bila sehat. Konsol
    /// memeriksanya tiap denyut pratinjau dan menghentikan siaran dengan pesan
    /// yang menyebut sebabnya.
    /// </summary>
    public string? Fault { get; private set; }

    public BroadcastSession(BroadcastSettings settings) => _settings = settings;

    public void SetTargets(IEnumerable<IPAddress> addresses) =>
        _sender.SetTargets(addresses.Select(a => new IPEndPoint(a, _settings.Port)));

    public void Start()
    {
        if (Running) return;

        // Tanpa baris ini, daftar tujuan pengirim kosong dan Send() keluar
        // diam-diam — loop tetap menangkap layar lalu membuang hasilnya.
        // Persis bug yang membuat penonton menerima nol frame sementara panel
        // admin menyatakan siaran berjalan.
        if (_settings.UnicastHost is { } host)
        {
            _sender.UseUnicast([new IPEndPoint(IPAddress.Parse(host), _settings.Port)]);
        }
        else
        {
            _sender.UseMulticast(_settings.MulticastGroup, _settings.Port);
        }

        _quality = _settings.Quality;
        _windowBytes = 0;
        _windowStart = Stopwatch.GetTimestamp();

        _source = new GdiScreenSource(_settings.TargetWidth, _settings.Quality, _settings.Target);
        Fault = null;
        _cts = new CancellationTokenSource();
        _uptime.Restart();
        _framesSent = 0;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private int _closedChecks;

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _settings.Fps));
        var nextTick = Stopwatch.GetTimestamp();
        var ticksPerInterval = (long)(interval.TotalSeconds * Stopwatch.Frequency);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_source is null) return;

                // Kesehatan target diperiksa dua kali sedetik, bukan tiap frame.
                if (Stopwatch.GetTimestamp() - _lastHealthCheck > Stopwatch.Frequency / 2)
                {
                    _lastHealthCheck = Stopwatch.GetTimestamp();

                    var health = _source.CheckTarget();
                    switch (health)
                    {
                        case TargetHealth.Closed:
                            // Beri toleransi beberapa detik sebelum benar-benar mematikan siaran,
                            // karena sebagian aplikasi (seperti PowerPoint slide show atau popout browser)
                            // merekonstruksi handle jendela saat beralih mode.
                            _closedChecks++;
                            if (_closedChecks >= 4)
                            {
                                Fault = $"{_source.TargetLabel} ditutup";
                                return;
                            }
                            _frozenByMinimize = true;
                            break;

                        case TargetHealth.Resized:
                            _closedChecks = 0;
                            _frozenByMinimize = false;
                            try { RebuildSource(); } catch { /* best effort */ }
                            break;

                        case TargetHealth.Minimized:
                            _closedChecks = 0;
                            _frozenByMinimize = true;
                            break;

                        case TargetHealth.Healthy:
                            _closedChecks = 0;
                            _frozenByMinimize = false;
                            break;
                    }
                }

                var holding = Frozen || _frozenByMinimize;

                try
                {
                    if (!holding)
                    {
                        var frame = _source.Grab();
                        if (frame is not null)
                        {
                            _sender.Send(frame);
                            _lastCaptured = frame;
                            LastFrame = frame.Data;
                            _framesSent++;

                            AdjustQuality(frame.Data.Length);
                        }
                    }
                    else if (_lastCaptured is not null
                          && Stopwatch.GetTimestamp() - _lastKeepAlive > Stopwatch.Frequency / 2)
                    {
                        _lastKeepAlive = Stopwatch.GetTimestamp();
                        _sender.Send(_lastCaptured);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Gangguan sesaat pada satu frame tidak boleh mematikan seluruh siaran.
                }

                nextTick += ticksPerInterval;
                var remaining = nextTick - Stopwatch.GetTimestamp();

                if (remaining <= 0)
                {
                    nextTick = Stopwatch.GetTimestamp();
                    continue;
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency), ct);
                }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Fault = $"Kesalahan siaran: {ex.Message}";
        }
    }

    /// <summary>
    /// Membuat ulang sumber tangkapan dengan ukuran jendela yang baru.
    /// Sumber lama dibuang setelah yang baru siap, supaya loop tidak pernah
    /// memegang referensi yang sudah di-dispose.
    /// </summary>
    /* ================= batas bitrate ================= */

    /// <summary>Anggaran laju keluar, dalam bit per detik.</summary>
    private const double BudgetBitsPerSecond = 25_000_000;

    private long _windowBytes;
    private long _windowStart;
    private int _quality;

    /// <summary>
    /// Menahan laju keluar dengan menurunkan kualitas JPEG.
    ///
    /// MENGAPA INI PERLU ADA SAMA SEKALI.
    ///
    /// Ukuran frame JPEG ditentukan ISI layar, bukan resolusinya. Layar dengan
    /// slide statis menghasilkan 80 KB; layar penuh gambar bergerak, atau layar
    /// yang memuat jendela penonton siarannya sendiri, menghasilkan 450 KB.
    /// Pada 15 fps itu 10 Mbps melawan 53 Mbps.
    ///
    /// Tanpa batas, siaran berjalan sempurna sepanjang pengujian dengan layar
    /// kosong lalu menjatuhkan seluruh kelas begitu dosen membuka video —
    /// kegagalan yang muncul tepat pada saat paling merugikan dan tidak pernah
    /// terlihat sebelumnya.
    ///
    /// Yang diturunkan KUALITAS, bukan resolusi. Mengganti resolusi di tengah
    /// siaran memaksa setiap penonton menskalakan ulang dan hasilnya terlihat
    /// jauh lebih buruk daripada gambar yang sedikit lebih lembut. Naik kembali
    /// dilakukan satu langkah pada satu waktu supaya laju tidak berayun.
    ///
    /// Setiap perubahan DICATAT. Kualitas yang turun diam-diam berarti dosen
    /// melihat gambarnya memburuk tanpa sebab yang dapat ditemukan siapa pun.
    /// </summary>
    private void AdjustQuality(int frameBytes)
    {
        _windowBytes += frameBytes;

        var elapsed = (Stopwatch.GetTimestamp() - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsed < 2) return;

        var bitsPerSecond = _windowBytes * 8 / elapsed;
        _windowBytes = 0;
        _windowStart = Stopwatch.GetTimestamp();

        if (_source is null) return;

        var before = _quality;

        if (bitsPerSecond > BudgetBitsPerSecond)
        {
            // Turun lebih tajam daripada naik: melewati anggaran merugikan
            // seluruh kelas sekarang, sedangkan kualitas yang tertinggal
            // sebentar hanya terlihat sedikit lebih lembut.
            _quality = Math.Max(45, _quality - 8);
        }
        else if (bitsPerSecond < BudgetBitsPerSecond * 0.7 && _quality < _settings.Quality)
        {
            _quality = Math.Min(_settings.Quality, _quality + 3);
        }

        if (_quality == before) return;

        _source.SetQuality(_quality);
        QualityChanged?.Invoke(_quality, bitsPerSecond / 1_000_000);
    }

    /// <summary>Dipicu setiap kualitas berubah: (kualitas baru, Mbps terukur).</summary>
    public event Action<int, double>? QualityChanged;

    private void RebuildSource()
    {
        // Kualitas yang SEDANG BERLAKU dibawa serta, bukan dikembalikan ke nilai
        // awal. Jendela yang diubah ukurannya di tengah siaran tidak mengubah
        // kapasitas jaringan lab; mengembalikannya ke 92 hanya akan melonjakkan
        // laju sekali lagi lalu diturunkan lagi dua detik kemudian.
        var replacement = new GdiScreenSource(
            _settings.TargetWidth,
            _quality > 0 ? _quality : _settings.Quality,
            _settings.Target);

        var previous = _source;
        _source = replacement;
        previous?.Dispose();
    }

    public BroadcastStatus Status()
    {
        var stats = _sender.Stats;
        var seconds = Math.Max(0.001, _uptime.Elapsed.TotalSeconds);
        return new BroadcastStatus(
            stats.FramesSent, stats.PacketsSent, stats.BytesSent, stats.SendErrors,
            _framesSent / seconds, _sender.TargetCount, Frozen);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        _uptime.Stop();
        _source?.Dispose();
        _source = null;
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sender.Dispose();
    }
}
