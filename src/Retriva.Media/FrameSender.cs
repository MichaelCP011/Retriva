using System.Net;
using System.Net.Sockets;
using Retriva.Shared.Broadcast;

namespace Retriva.Media;

public sealed record SenderStats(
    long FramesSent, long PacketsSent, long BytesSent, long SendErrors);

/// <summary>
/// Mengirim frame lewat UDP, multicast maupun unicast.
///
/// MULTICAST adalah bawaan. Satu salinan paket dikirim, lalu switch yang
/// menduplikasinya ke setiap penerima — sehingga bandwidth tidak bergantung
/// pada jumlah unit sama sekali. Untuk 40 penerima, bedanya empat puluh kali.
///
/// Semula aku memilih unicast karena TL-SG1024D adalah switch unmanaged dan
/// multicast tanpa IGMP querier bisa mati diam-diam. Kehati-hatian itu
/// terbantahkan oleh bukti: prototipe multicast di lab ini sudah dijalankan
/// pada 40 PC sekaligus dan stabil. Pengukuran di perangkat sungguhan
/// mengalahkan kekhawatiran teoretis.
///
/// Unicast tetap dipertahankan sebagai cadangan bila suatu saat multicast
/// bermasalah — misalnya setelah pergantian perangkat jaringan.
/// </summary>
public sealed class FrameSender : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _packet = new byte[FramePacket.MaxPacketSize];
    private IPEndPoint[] _targets = [];
    private uint _frameId;

    private long _frames, _packets, _bytes, _errors;

    public FrameSender()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Buffer kirim diperbesar. Satu frame 83 KB terpecah jadi sekitar 60
        // paket yang dikirim beruntun; buffer bawaan bisa penuh dan menolak
        // sebagian di tengah burst.
        _socket.SendBufferSize = 1 << 20;
    }

    public bool IsMulticast { get; private set; }

    /// <summary>
    /// Menyiarkan ke grup multicast. Loopback diaktifkan supaya klien di PC
    /// admin sendiri ikut menerima — berguna untuk memverifikasi siaran tanpa
    /// perlu melihat ke layar mahasiswa.
    /// </summary>
    public void UseMulticast(string group = FramePacket.MulticastGroup,
                             int port = FramePacket.DefaultPort,
                             int ttl = FramePacket.MulticastTtl)
    {
        var address = IPAddress.Parse(group);

        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

        _targets = [new IPEndPoint(address, port)];
        IsMulticast = true;
    }

    /// <summary>Cadangan: mengirim salinan terpisah ke tiap penerima.</summary>
    public void UseUnicast(IEnumerable<IPEndPoint> targets)
    {
        _targets = targets.ToArray();
        IsMulticast = false;
    }

    public void SetTargets(IEnumerable<IPEndPoint> targets) => UseUnicast(targets);

    public int TargetCount => _targets.Length;

    public SenderStats Stats => new(_frames, _packets, _bytes, _errors);

    /// <summary>
    /// Menyebar satu frame. Paket yang sama dikirim berulang ke tiap penerima,
    /// jadi biaya jaringan tumbuh linear terhadap jumlah unit — konsekuensi
    /// unicast yang sudah diperhitungkan dari pengukuran bandwidth.
    /// </summary>
    public void Send(CapturedFrame frame)
    {
        if (_targets.Length == 0) return;

        var data = frame.Data;
        var chunkCount = FramePacket.ChunkCountFor(data.Length);
        if (chunkCount is 0 or > ushort.MaxValue) return;

        var frameId = unchecked(++_frameId);

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var offset = chunk * FramePacket.MaxPayloadSize;
            var length = Math.Min(FramePacket.MaxPayloadSize, data.Length - offset);

            FramePacket.WriteHeader(_packet, frameId, (ushort)chunk, (ushort)chunkCount,
                (ushort)frame.Width, (ushort)frame.Height);
            data.AsSpan(offset, length).CopyTo(_packet.AsSpan(FramePacket.HeaderSize));

            var packet = _packet.AsSpan(0, FramePacket.HeaderSize + length);

            foreach (var target in _targets)
            {
                try
                {
                    _socket.SendTo(packet, SocketFlags.None, target);
                    _packets++;
                    _bytes += packet.Length;
                }
                catch (SocketException)
                {
                    // Satu penerima bermasalah tidak boleh menghentikan yang lain.
                    _errors++;
                }
            }
        }

        _frames++;
    }

    public void Dispose() => _socket.Dispose();
}
