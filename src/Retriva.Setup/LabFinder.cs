using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Retriva.Shared.Protocol;

namespace Retriva.Setup;

/// <summary>
/// Mendengarkan beacon koordinator, supaya alamatnya tidak perlu diketik.
///
/// ALAMAT YANG DIKETIK ADALAH MEDAN YANG PALING SERING SALAH.
///
/// Nilai bawaan di wizard hanyalah tebakan — 192.168.1.252 benar di lab yang
/// memakai subnet itu dan salah di setiap tempat lain, termasuk di mesin
/// pengembang yang menguji secara lokal. Orang yang memasang lalu melihat
/// "tidak menjawab dalam 3 detik" tidak punya cara mengetahui alamat yang
/// benar tanpa membuka panel di mesin lain dan membacanya.
///
/// Koordinator sudah memancarkan beacon tiap dua detik untuk penemuan lab oleh
/// panel dosen. Installer mendengarkan siaran yang sama — infrastrukturnya
/// sudah ada, dan yang kurang hanya pemakaiannya di sini.
///
/// Sidik jari sertifikat ikut di dalam beacon, jadi ia juga terisi sendiri.
/// Dua medan yang paling mudah salah ketik menjadi nol.
///
/// TIDAK DIPERCAYA SEBAGAI KEAMANAN. Siapa pun di jaringan yang sama dapat
/// mengirim datagram yang mengaku lab. Yang didapat dari sini hanyalah isian
/// awal yang dapat dibaca dan diubah orangnya sebelum melanjutkan — sama
/// seperti alamat yang ia ketik sendiri.
/// </summary>
public static class LabFinder
{
    /// <summary>
    /// Mendengarkan beberapa detik, lalu mengembalikan lab yang terdengar.
    ///
    /// Beacon dipancarkan tiap dua detik, jadi empat detik cukup untuk menangkap
    /// setidaknya satu dari tiap koordinator yang aktif. Lebih lama hanya
    /// membuat orangnya menunggu tanpa menambah apa yang ditemukan.
    /// </summary>
    public static List<LabBeacon> Listen(TimeSpan patience)
    {
        var found = new Dictionary<string, LabBeacon>(StringComparer.OrdinalIgnoreCase);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            // ReuseAddress WAJIB: koordinator di mesin yang sama sudah memegang
            // porta ini. Tanpanya, memasang agent di PC Asisten akan gagal
            // mendengar beacon dari koordinator yang berjalan tepat di sebelahnya.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, PanelProtocol.DiscoveryPort));

            JoinOnEveryInterface(socket);

            socket.ReceiveTimeout = 400;
            var buffer = new byte[8192];
            var deadline = DateTime.UtcNow + patience;

            while (DateTime.UtcNow < deadline)
            {
                /*
                    ReceiveFrom, BUKAN Receive — dan itu bukan detail kecil.

                    Koordinator sengaja mengirim Host kosong. Mesin dengan
                    beberapa kartu jaringan tidak dapat mengetahui lewat mana ia
                    terlihat oleh penerima: VPN, adaptor virtual, kartu kedua,
                    semuanya punya alamat yang benar dari sudut pandangnya
                    sendiri. Alamat PENGIRIM datagram adalah satu-satunya yang
                    dijamin merupakan jalur yang benar-benar sampai.

                    Versi pertama fungsi ini memakai Receive, yang membuang
                    alamat pengirim, lalu membuang setiap beacon karena Host-nya
                    kosong. Tombol pencarian akan selalu melaporkan "tidak ada
                    koordinator terdengar" — persis pada jaringan tempat
                    koordinatornya jelas-jelas ada.
                */
                int length;
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

                try { length = socket.ReceiveFrom(buffer, ref sender); }
                catch (SocketException) { continue; }   // batas waktu; coba lagi

                if (sender is not IPEndPoint from) continue;

                LabBeacon? beacon;
                try
                {
                    beacon = JsonSerializer.Deserialize<LabBeacon>(
                        Encoding.UTF8.GetString(buffer, 0, length),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException)
                {
                    // Datagram yang bukan beacon Retriva. Porta multicast dipakai
                    // bersama, dan mengeluh tentang paket orang lain tidak berguna.
                    continue;
                }

                if (beacon is null || beacon.Port <= 0) continue;

                var host = from.Address.ToString();

                // Dikunci pada host, bukan ditambahkan berulang. Beacon yang sama
                // tiba beberapa kali selama masa dengar.
                found[host] = beacon with { Host = host };
            }
        }
        catch (SocketException)
        {
            // Multicast diblokir, atau porta tidak dapat diikat. Bukan kegagalan
            // pemasangan — alamatnya tinggal diketik seperti sebelumnya.
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Bergabung ke grup lewat SETIAP antarmuka.
    ///
    /// Bergabung tanpa menyebut antarmuka menyerahkan pilihannya ke tabel rute
    /// sistem, yang pada mesin dengan VirtualBox atau VPN sering memilih
    /// adapter virtual — dan beacon dari jaringan lab yang sebenarnya tidak
    /// pernah terdengar. Pelajaran yang sama dengan yang membuat relay siaran
    /// memancar ke semua antarmuka.
    /// </summary>
    private static void JoinOnEveryInterface(Socket socket)
    {
        var group = IPAddress.Parse(PanelProtocol.DiscoveryGroup);
        var joined = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, address.Address));
                    joined++;
                }
                catch (SocketException)
                {
                    // Antarmuka ini menolak. Yang lain mungkin tidak — dan satu
                    // penolakan tidak boleh membatalkan seluruh pencarian.
                }
            }
        }

        if (joined == 0)
            throw new SocketException((int)SocketError.NetworkDown);
    }
}
