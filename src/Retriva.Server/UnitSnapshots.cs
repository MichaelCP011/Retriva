using Retriva.Server.Data;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Menyusun daftar unit sebagaimana dilihat panel.
///
/// Ada dua tempat yang membutuhkannya — PanelHub saat panel meminta daftar
/// pertama kali, dan PanelBroadcaster saat mendorong perubahan berikutnya.
/// Menulisnya dua kali berarti daftar awal dan pembaruannya dapat berbeda isi,
/// dan bedanya baru terlihat sebagai kolom yang terisi saat dibuka lalu kosong
/// beberapa detik kemudian — persis jenis kesalahan yang tidak memunculkan
/// galat apa pun dan sulit dipercaya ketika dilaporkan.
/// </summary>
public static class UnitSnapshots
{
    /// <summary>
    /// <paramref name="includeThumbnails"/> hanya benar untuk pemuatan pertama.
    ///
    /// Empat puluh gambar base64 berukuran ratusan kilobyte. Menyertakannya di
    /// setiap dorongan berkala berarti mengirim ulang seluruhnya dua kali per
    /// detik, padahal yang berubah biasanya satu. Pratinjau berikutnya dikirim
    /// satu per satu lewat dorongan "Thumbnail" begitu tiba.
    ///
    /// Tetapi pemuatan pertama memang perlu membawanya: panel yang baru dibuka
    /// akan menampilkan empat puluh kotak kosong sampai setiap unit kebetulan
    /// mengirim pratinjau berikutnya — terlihat persis seperti lab yang mati.
    /// </summary>
    public static UnitSnapshot[] Build(UnitRegistry registry, UnitStore units, ChatStore chats,
                                       bool includeThumbnails)
    {
        var unread = chats.UnreadByUnit();
        var aliases = units.Aliases();
        var selfNames = units.SelfNames();
        var blocked = units.Blocked();
        var fullscreen = units.AutoFullscreen();

        return registry.Snapshot().Select(u => new UnitSnapshot
        {
            UnitId = u.UnitId,
            Alias = aliases.GetValueOrDefault(u.UnitId),

            /*
                Dikirim TERPISAH dari alias, tidak digabung di sini.

                Menggabungkannya — "Alias = alias ?? selfName" — akan membuat
                panel tidak dapat lagi membedakan nama yang ditetapkan asisten
                dari nama yang diketik mahasiswa. Layar konfigurasi lalu
                menampilkan nama mahasiswa di kotak alias yang kosong, dan
                asisten yang menekan Simpan tanpa mengubah apa pun akan
                mengangkatnya menjadi alias sungguhan tanpa pernah bermaksud
                begitu.

                Panel yang memutuskan mana yang ditampilkan, dan ia hanya bisa
                memutuskan bila keduanya sampai utuh.
            */
            SelfName = selfNames.GetValueOrDefault(u.UnitId),
            Blocked = blocked.Contains(u.UnitId),
            AutoFullscreen = fullscreen.Contains(u.UnitId),
            StudentNim = u.StudentNim,
            StudentName = u.StudentName,
            IsGuest = u.IsGuest,
            Status = u.Status,
            IpAddress = u.IpAddress,
            HostName = u.HostName,
            MacAddress = u.MacAddress,
            WiredConnection = u.WiredConnection,
            LinkSpeedMbps = u.LinkSpeedMbps,
            NetworkAdapter = u.NetworkAdapter,
            CoordinatorHost = u.CoordinatorHost,
            LatencyMs = u.LatencyMs,
            LastSeen = u.LastSeen,
            ThumbnailDataUrl = includeThumbnails ? u.ThumbnailDataUrl : null,
            HelpRequestedAt = u.HelpRequestedAt,
            Unread = unread.GetValueOrDefault(u.UnitId, 0),
        }).ToArray();
    }
}
