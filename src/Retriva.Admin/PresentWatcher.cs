using Retriva.Media;
using Retriva.Shared.Broadcast;

namespace Retriva.Admin;

/// <summary>
/// Menonton siaran yang sedang berjalan, untuk ditampilkan di pratinjau panel.
///
/// ADA KARENA PANEL TIDAK SELALU MENJADI SUMBERNYA.
///
/// Saat dosen menyiarkan layarnya sendiri, pratinjau dapat dibaca langsung dari
/// sesi tangkapan yang berjalan di panel ini. Saat yang disiarkan adalah layar
/// mahasiswa, panel tidak menangkap apa pun — dan kotak pratinjaunya kosong.
///
/// Dosen yang menyuruh mahasiswa presentasi lalu melihat kotak kosong tidak
/// punya cara mengetahui apakah layarnya benar-benar terkirim. Ia akan menekan
/// tombolnya lagi, memilih unit lain, atau menyimpulkan fiturnya rusak —
/// padahal di seluruh kelas gambarnya sudah tampil.
///
/// Yang ditampilkan di sini adalah frame yang BENAR-BENAR keluar dari relay,
/// diterima lewat jalur yang sama dengan yang dipakai mahasiswa. Bukan tebakan
/// tentang apa yang mungkin terkirim — melainkan salinan dari apa yang mereka
/// lihat.
/// </summary>
public sealed class PresentWatcher : IDisposable
{
    private readonly FrameReceiver _receiver;

    /// <summary>Frame terakhir yang utuh, kosong sampai yang pertama tiba.</summary>
    public byte[] LastFrame { get; private set; } = [];

    public long Frames => _receiver.Stats.FramesComplete;
    public long Packets => _receiver.Stats.PacketsReceived;

    public PresentWatcher()
    {
        _receiver = new FrameReceiver(FramePacket.DefaultPort, FramePacket.MulticastGroup);
        _receiver.FrameReady += frame => LastFrame = frame.Jpeg;
        _receiver.Start();
    }

    public void Dispose()
    {
        _receiver.Dispose();
        LastFrame = [];
    }
}
