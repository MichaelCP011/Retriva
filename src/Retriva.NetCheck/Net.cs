using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Retriva.NetCheck;

/// <summary>Satu antarmuka IPv4 beserta sifat yang menentukan nasib multicast.</summary>
public sealed record Nic(
    IPAddress Address,
    string Name,
    bool SupportsMulticast,
    bool IsWireless,
    bool IsVirtual,
    bool IsApipa,
    long SpeedMbps);

/// <summary>
/// Pengetahuan jaringan bersama, dipakai semua mode NetCheck.
///
/// TETAP TANPA SATU BARIS PUN KODE RETRIVA.
///
/// Aturan itu diwarisi dari versi pertama alat ini dan tidak dilonggarkan.
/// Gunanya membuktikan apakah jaringannya bekerja, terlepas dari Retriva —
/// begitu ia memakai FramePacket atau FrameReceiver, kegagalannya tidak lagi
/// dapat dibedakan dari bug di kode kita, dan justru itu yang sedang
/// dieliminasi.
///
/// Konstanta di bawah SENGAJA disalin, bukan dirujuk. Kalau suatu saat nilainya
/// berbeda dengan yang dipakai Retriva, alat ini akan melaporkan jaringan yang
/// sehat untuk grup yang salah — dan itu justru temuan yang berguna.
/// </summary>
public static class Net
{
    public const string Group = "239.77.82.84";
    public const int BroadcastPort = 9100;
    public const int ControlPort = 9199;
    public const int PacketSize = 1400;

    /// <summary>Penanda muatan uji, agar paket asing tidak ikut terhitung.</summary>
    public const string Magic = "RTVNC1";

    /// <summary>
    /// Antarmuka IPv4 yang layak dipertimbangkan, beserta alasan mencurigainya.
    ///
    /// APIPA (169.254.x) DIMASUKKAN, tidak dibuang diam-diam. Alamat itu berarti
    /// DHCP gagal, dan mesin yang memilikinya tidak akan pernah menjangkau
    /// koordinator. Membuangnya dari daftar membuat gejalanya hilang dari
    /// laporan sekaligus dari perhatian orang yang membacanya.
    /// </summary>
    public static List<Nic> Interfaces()
    {
        var list = new List<Nic>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var bytes = addr.Address.GetAddressBytes();
                var name = nic.Name;
                var desc = nic.Description;

                list.Add(new Nic(
                    Address: addr.Address,
                    Name: name,
                    SupportsMulticast: nic.SupportsMulticast,
                    IsWireless: nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211,
                    IsVirtual: LooksVirtual(name) || LooksVirtual(desc),
                    IsApipa: bytes is [169, 254, ..],
                    SpeedMbps: nic.Speed > 0 ? nic.Speed / 1_000_000 : 0));
            }
        }

        return list;
    }

    /// <summary>
    /// Antarmuka virtual dikenali dari NAMANYA, dan itu memang rapuh.
    ///
    /// Tidak ada tanda resmi di Windows maupun Linux yang menyatakan sebuah
    /// adapter milik VirtualBox, VMware, atau Hyper-V. Daftar kata ini akan
    /// meleset pada adapter yang belum disebut.
    ///
    /// Konsekuensi melesetnya ringan dan disengaja: adapter itu hanya kehilangan
    /// label "virtual" di laporan. Ia tetap diuji, tetap dihitung, dan tetap
    /// muncul. Label ini membantu manusia membaca, bukan menentukan perilaku.
    /// </summary>
    private static bool LooksVirtual(string text)
    {
        string[] marks = ["virtualbox", "vmware", "hyper-v", "vethernet", "loopback adapter",
                          "tap-windows", "wireguard", "openvpn", "docker", "vmnet", "vboxnet"];

        return marks.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Antarmuka yang dipakai untuk mengirim atau menerima multicast.</summary>
    public static List<Nic> MulticastCapable() =>
        [.. Interfaces().Where(n => n.SupportsMulticast && !n.IsApipa)];

    /// <summary>
    /// Membuka soket penerima multicast yang bergabung lewat SETIAP antarmuka.
    ///
    /// Bergabung tanpa menyebut antarmuka menyerahkan pilihannya ke tabel rute
    /// sistem, yang pada mesin dengan VirtualBox atau VPN sering memilih adapter
    /// virtual — dan datagram dari jaringan lab yang sebenarnya tidak pernah
    /// terdengar. Pelajaran yang sama dengan yang membuat relay siaran Retriva
    /// memancar ke semua antarmuka.
    /// </summary>
    public static Socket OpenReceiver(int port, out List<string> joined, out List<string> refused)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));

        // Buffer besar, sama alasannya dengan FrameReceiver: satu frame tiba
        // sebagai puluhan paket beruntun, dan buffer bawaan meluap pada ledakan
        // yang di loopback tidak pernah terjadi.
        socket.ReceiveBufferSize = 4 << 20;

        joined = [];
        refused = [];

        foreach (var nic in MulticastCapable())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(IPAddress.Parse(Group), nic.Address));
                joined.Add(nic.Address.ToString());
            }
            catch (SocketException ex)
            {
                refused.Add($"{nic.Address} ({ex.SocketErrorCode})");
            }
        }

        return socket;
    }

    /// <summary>Membuka satu soket pemancar per antarmuka.</summary>
    public static List<(Nic Nic, Socket Socket)> OpenSenders(out List<string> refused)
    {
        var senders = new List<(Nic, Socket)>();
        refused = [];

        foreach (var nic in MulticastCapable())
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(nic.Address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                senders.Add((nic, socket));
            }
            catch (SocketException ex)
            {
                refused.Add($"{nic.Address} ({ex.SocketErrorCode})");
            }
        }

        return senders;
    }

    /// <summary>Muatan uji: penanda, nomor urut, penanda sesi, lalu padding.</summary>
    public static byte[] Payload(int seq, string tag, int size)
    {
        var head = $"{Magic}|{seq}|{tag}|";
        var buffer = new byte[Math.Max(size, head.Length)];

        System.Text.Encoding.ASCII.GetBytes(head).CopyTo(buffer, 0);
        for (var i = head.Length; i < buffer.Length; i++) buffer[i] = (byte)'.';

        return buffer;
    }

    /// <summary>Membaca nomor urut dari muatan uji. Null berarti bukan paket kita.</summary>
    public static int? ReadSeq(ReadOnlySpan<byte> buffer, string tag)
    {
        if (buffer.Length < Magic.Length) return null;

        var text = System.Text.Encoding.ASCII.GetString(
            buffer[..Math.Min(buffer.Length, 64)]);

        if (!text.StartsWith(Magic, StringComparison.Ordinal)) return null;

        var parts = text.Split('|');
        if (parts.Length < 3) return null;
        if (!string.Equals(parts[2], tag, StringComparison.Ordinal)) return null;

        return int.TryParse(parts[1], out var seq) ? seq : null;
    }

    /// <summary>Menguji apakah ada yang mendengarkan di sebuah porta TCP.</summary>
    public static async Task<(bool Ok, long Ms, string Detail)> ProbeTcp(
        string host, int port, int timeoutMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);

            await client.ConnectAsync(host, port, cts.Token);
            watch.Stop();
            return (true, watch.ElapsedMilliseconds, "tersambung");
        }
        catch (OperationCanceledException)
        {
            return (false, timeoutMs, $"tidak menjawab dalam {timeoutMs} ms");
        }
        catch (SocketException ex)
        {
            // ConnectionRefused BERBEDA ARTINYA dari timeout, dan bedanya
            // menentukan langkah berikutnya. Ditolak berarti mesinnya ada dan
            // menjawab, hanya tidak ada yang mendengarkan di porta itu —
            // layanannya mati. Timeout berarti paketnya tidak sampai sama
            // sekali — firewall, atau alamat yang salah.
            var detail = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    "ditolak — mesinnya hidup, tetapi tidak ada yang mendengarkan di porta ini",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                    "tidak terjangkau — beda subnet, atau rute tidak ada",
                SocketError.TimedOut =>
                    "batas waktu — kemungkinan diblokir firewall",
                _ => ex.SocketErrorCode.ToString(),
            };

            return (false, watch.ElapsedMilliseconds, detail);
        }
    }
}
