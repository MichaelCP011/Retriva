namespace Retriva.FakeAgent;

/// <summary>
/// Membuat JPEG kecil tanpa pustaka gambar apa pun.
///
/// Simulator sengaja dijaga tetap platform-netral supaya 40 instance ringan
/// dan bisa dijalankan di mesin mana saja, jadi System.Drawing tidak dipakai.
/// Yang dihasilkan adalah JPEG berukuran 8x8 blok warna solid — cukup untuk
/// membuktikan jalur pengiriman pratinjau bekerja dan tiap unit terbedakan.
/// </summary>
internal static class SyntheticThumbnail
{
    public const int Width = 8;
    public const int Height = 8;

    /// <summary>
    /// JPEG grayscale 8x8 minimal. Satu-satunya bagian yang berubah adalah
    /// nilai DC pada data terkompresi, yang menentukan tingkat kecerahan blok.
    /// </summary>
    public static byte[] Create(string unitId, int frame)
    {
        // Kecerahan diturunkan dari nomor unit dan nomor frame, sehingga tiap
        // unit punya rona berbeda dan grid terlihat berdenyut pelan.
        var seed = unitId.Sum(c => c) + frame * 7;
        var level = (byte)(40 + seed % 180);
        return BuildJpeg(level);
    }

    private static byte[] BuildJpeg(byte level)
    {
        var bytes = new List<byte>(Template.Length + 8);
        bytes.AddRange(Template);

        // Blok 8x8 satu warna: cukup mengganti byte data terakhir sebelum EOI.
        var eoi = bytes.Count - 2;
        bytes[eoi - 1] = level;
        return bytes.ToArray();
    }

    /// <summary>
    /// Kerangka JPEG baseline 8x8 grayscale: SOI, tabel kuantisasi, SOF0,
    /// tabel Huffman standar, SOS, satu blok data, lalu EOI.
    /// </summary>
    private static readonly byte[] Template =
    [
        0xFF, 0xD8,                                     // SOI
        0xFF, 0xDB, 0x00, 0x43, 0x00,                   // DQT
        .. Enumerable.Repeat((byte)0x10, 64),
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08, 0x01, 0x01, 0x11, 0x00, // SOF0 8x8 grayscale
        0xFF, 0xC4, 0x00, 0x1F, 0x00,                   // DHT DC
        0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B,
        0xFF, 0xC4, 0x00, 0x14, 0x10, 0x00, 0x01,       // DHT AC (minimal)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, // SOS
        0x80,                                            // data satu blok
        0xFF, 0xD9,                                      // EOI
    ];
}
