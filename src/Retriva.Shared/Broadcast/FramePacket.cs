using System.Buffers.Binary;

namespace Retriva.Shared.Broadcast;

/// <summary>
/// Format paket siaran layar di atas UDP.
///
/// Satu frame JPEG berukuran 30–150 KB, jauh melebihi MTU, jadi harus dipecah.
/// Tidak ada pengiriman ulang: frame berikutnya datang dalam puluhan milidetik,
/// sehingga menunggu paket yang hilang justru menambah jeda tanpa memperbaiki
/// apa pun. Frame yang tidak lengkap dibuang, dan penonton melihat satu frame
/// terlewat — nyaris tak terasa. Inilah keunggulan MJPEG dibanding H.264:
/// setiap frame berdiri sendiri, jadi kehilangan tidak merambat.
/// </summary>
public static class FramePacket
{
    /// <summary>Penanda agar paket asing tidak salah ditafsirkan.</summary>
    public const uint Magic = 0x52545646; // "RTVF"

    public const int HeaderSize = 16;

    /// <summary>
    /// 1400 byte aman melewati Ethernet tanpa fragmentasi IP. Fragmentasi di
    /// lapisan IP berbahaya di sini: satu fragmen hilang menjatuhkan seluruh
    /// datagram, sehingga kerugiannya berlipat dibanding memecah sendiri.
    /// </summary>
    public const int MaxPacketSize = 1400;

    public const int MaxPayloadSize = MaxPacketSize - HeaderSize;

    /// <summary>
    /// Grup multicast untuk siaran layar. Rentang 239.x.x.x adalah
    /// administratively scoped — tidak pernah diteruskan keluar jaringan lokal,
    /// jadi aman dipakai tanpa koordinasi dengan siapa pun.
    /// </summary>
    public const string MulticastGroup = "239.77.82.84";  // 'R','T' dalam desimal

    public const int DefaultPort = 9100;

    /// <summary>
    /// Port terpisah untuk aliran kendali jarak jauh.
    ///
    /// Arahnya berlawanan dengan siaran, jadi keduanya bisa berjalan bersamaan:
    /// dosen menyiarkan slide ke seluruh lab sementara asisten membantu satu
    /// mahasiswa. Kalau portnya sama, kedua aliran bercampur di soket yang sama
    /// dan penerima menyusun ulang potongan frame dari dua sumber berbeda.
    /// </summary>
    public const int RemoteControlPort = 9101;

    /// <summary>
    /// TTL 2 cukup melewati router lab bila kelak ada segmen tambahan,
    /// tetapi tidak akan menyeberang lebih jauh dari itu.
    /// </summary>
    public const int MulticastTtl = 2;

    public static int ChunkCountFor(int frameBytes) =>
        (frameBytes + MaxPayloadSize - 1) / MaxPayloadSize;

    public static void WriteHeader(
        Span<byte> buffer, uint frameId, ushort chunkIndex, ushort chunkCount,
        ushort width, ushort height)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[..4], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8, 2), chunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(10, 2), chunkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12, 2), width);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14, 2), height);
    }

    public static bool TryReadHeader(
        ReadOnlySpan<byte> buffer,
        out uint frameId, out ushort chunkIndex, out ushort chunkCount,
        out ushort width, out ushort height)
    {
        frameId = 0; chunkIndex = 0; chunkCount = 0; width = 0; height = 0;
        if (buffer.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]) != Magic) return false;

        frameId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(8, 2));
        chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(10, 2));
        width = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14, 2));
        return chunkCount > 0 && chunkIndex < chunkCount;
    }
}
