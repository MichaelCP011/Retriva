using System.Runtime.Versioning;
using Retriva.Media;
using Retriva.Shared.Broadcast;

namespace Retriva.Admin;

/// <summary>
/// Sisi admin dari kendali jarak jauh: menerima aliran layar satu mahasiswa.
///
/// Arahnya berlawanan dengan siaran, jadi peran soketnya bertukar — di sini
/// admin yang mendengarkan dan klien yang mengirim. Portnya pun terpisah dari
/// port siaran supaya dosen tetap dapat menyiarkan slide ke seluruh lab
/// sementara asisten membantu satu mahasiswa.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteView : IDisposable
{
    private FrameReceiver? _receiver;

    /// <summary>Unit yang sedang dilihat, atau null bila tidak ada sesi.</summary>
    public string? UnitId { get; private set; }

    public bool InputAllowed { get; private set; }

    /// <summary>Frame terakhir yang utuh, sebagai JPEG.</summary>
    public byte[] LastFrame { get; private set; } = [];

    /// <summary>Ukuran asli frame, dipakai UI untuk menjaga rasio aspek.</summary>
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }

    public long FramesReceived { get; private set; }
    public DateTimeOffset? LastFrameAt { get; private set; }

    public bool Active => _receiver is not null;

    /// <summary>
    /// Sudah berapa lama tidak ada frame masuk.
    ///
    /// Ini satu-satunya cara admin mengetahui aliran mati tanpa menunggu
    /// laporan: klien yang crash tidak mengirim apa pun, termasuk kabar bahwa
    /// ia berhenti. Frame terakhir akan membeku di layar dan tampak seperti
    /// mahasiswa yang sedang tidak menyentuh apa-apa.
    /// </summary>
    public TimeSpan SinceLastFrame =>
        LastFrameAt is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - LastFrameAt.Value;

    public void Start(string unitId, bool allowInput)
    {
        Stop();

        UnitId = unitId;
        InputAllowed = allowInput;
        FramesReceived = 0;
        LastFrame = [];
        LastFrameAt = null;

        // Argumen kedua WAJIB null. Parameter grup punya nilai bawaan berupa
        // grup multicast siaran, jadi memanggil dengan port saja akan membuat
        // penerima ini ikut bergabung ke grup itu — lalu menerima frame siaran
        // dosen di port yang salah dan mencampurnya dengan aliran mahasiswa.
        _receiver = new FrameReceiver(FramePacket.RemoteControlPort, multicastGroup: null);
        _receiver.FrameReady += OnFrame;
        _receiver.Start();
    }

    private void OnFrame(ReceivedFrame frame)
    {
        LastFrame = frame.Jpeg;
        FrameWidth = frame.Width;
        FrameHeight = frame.Height;
        FramesReceived++;
        LastFrameAt = DateTimeOffset.UtcNow;
    }

    public void Stop()
    {
        if (_receiver is null) return;
        _receiver.FrameReady -= OnFrame;
        _receiver.Dispose();
        _receiver = null;
        UnitId = null;
        InputAllowed = false;
    }

    public void Dispose() => Stop();
}
