using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Retriva.NetCheck;

/*
    Alat uji multicast yang berdiri sendiri.

    MENGAPA TERPISAH DARI RETRIVA.

    Siaran gagal dengan gejala yang paling sulit ditelusuri: pengirim melaporkan
    puluhan ribu datagram terkirim pada belasan Mbps tanpa satu pun galat, dan
    penerima menampilkan layar hitam. Setiap tebakan tentang penyebabnya —
    antarmuka mana yang benar, opsi soket mana yang kurang — terlihat masuk akal
    dan tidak satu pun dapat dibuktikan dari dalam program yang sedang diperiksa.

    Alat ini memotong lingkarannya. Ia tidak memakai satu baris pun kode Retriva:
    tidak FramePacket, tidak FrameSender, tidak FrameReceiver. Kalau multicast
    bekerja di sini tetapi gagal di Retriva, penyebabnya ada di kode kita. Kalau
    gagal di sini juga, penyebabnya ada di mesin atau jaringannya — firewall,
    tabel rute, adaptor virtual — dan tidak ada perubahan kode yang akan
    memperbaikinya.

    ============================================================
    DUA MODE, DAN YANG KEDUA MENJAWAB PERTANYAAN YANG BERBEDA.
    ============================================================

    MODE MENTAH — listen / send / both

        Membuktikan multicast bekerja di mesin ini, titik. Berguna saat yang
        dicurigai adalah satu mesin, bukan hubungan antara dua mesin.

            dotnet run --project src/Retriva.NetCheck -- both

    MODE DIAGNOSA — server / client

        Menjawab pertanyaan yang sebenarnya dihadapi: "mengapa jalan di lokal
        tetapi mati di lab". Ia menguji seluruh rantai antara DUA mesin secara
        berurutan, lalu mencetak vonis beserta langkah perbaikannya.

        Di PC Asisten:      RetrivaNetCheck server
        Di PC mahasiswa:    RetrivaNetCheck client --host 192.168.1.2

        Dijalankan SATU ORANG. Sisi koordinator menunggu; sisi klien yang
        menyetir seluruh urutan lewat saluran kendali TCP, jadi tidak ada yang
        perlu ditekan bersamaan di dua ruangan.

    Alat ini berjalan di Linux juga (net10.0 polos, tanpa API Windows), sehingga
    VM Ubuntu dapat berperan sebagai node kedua tanpa lisensi Windows.
*/

const string Group = "239.77.82.84";
const int Port = 9100;

var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "both";

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

/* ---------------- mode diagnosa ---------------- */

if (mode is "server")
{
    await Server.RunAsync(cts.Token);
    return 0;
}

if (mode is "client")
{
    var host = Value("--host");

    if (string.IsNullOrWhiteSpace(host))
    {
        Console.Error.WriteLine("GAGAL: --host wajib diisi.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  RetrivaNetCheck client --host 192.168.1.2");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Alamatnya dicetak jendela \"RetrivaNetCheck server\" di PC Asisten.");
        return 1;
    }

    var retrivaPort = int.TryParse(Value("--port"), out var p) ? p : 8443;
    return await Client.RunAsync(host, retrivaPort, cts.Token);
}

/* ---------------- mode mentah ---------------- */

Console.WriteLine($"Retriva NetCheck · grup {Group} · port {Port} · mode {mode}");
Console.WriteLine();

PrintInterfaces();

var tasks = new List<Task>();

if (mode is "listen" or "both") tasks.Add(Listen(cts.Token));

// Pengirim diberi jeda supaya penerima di proses yang sama sudah bergabung ke
// grup sebelum datagram pertama lewat. Tanpa jeda, hasil "0 diterima" bisa
// berarti "belum sempat bergabung" — kegagalan palsu yang menyesatkan.
if (mode is "send" or "both") tasks.Add(Send(cts.Token, delayMs: mode is "both" ? 1500 : 0));

await Task.WhenAll(tasks);
return 0;

// Menerima "--nama nilai" maupun "--nama=nilai". Komentar biasa, bukan XML doc:
// fungsi lokal tidak dapat membawa dokumentasi XML, dan compiler mengeluh CS1587.
string? Value(string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            return args[i][(name.Length + 1)..];

        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1];
    }
    return null;
}

static void PrintInterfaces()
{
    Console.WriteLine("Antarmuka IPv4 yang aktif dan mendukung multicast:");

    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up) continue;

        foreach (var addr in nic.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

            var flags = new List<string>();
            if (!nic.SupportsMulticast) flags.Add("TANPA MULTICAST");
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) flags.Add("loopback");

            Console.WriteLine($"  {addr.Address,-16} {nic.Name}"
                            + (flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : ""));
        }
    }

    Console.WriteLine();
}

static List<IPAddress> MulticastAddresses() =>
[
    .. NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
        .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
];

static async Task Listen(CancellationToken ct)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Any, Port));

    var joined = 0;

    foreach (var local in MulticastAddresses())
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(Group), local));

            Console.WriteLine($"[dengar] bergabung ke grup lewat {local}");
            joined++;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[dengar] GAGAL bergabung lewat {local}: {ex.SocketErrorCode}");
        }
    }

    if (joined == 0)
    {
        Console.WriteLine("[dengar] TIDAK bergabung ke satu antarmuka pun. "
                        + "Multicast tidak akan diterima.");
    }

    Console.WriteLine($"[dengar] menunggu di :{Port} …");
    Console.WriteLine();

    var buffer = new byte[65536];
    var from = new IPEndPoint(IPAddress.Any, 0) as EndPoint;
    var count = 0;

    while (!ct.IsCancellationRequested)
    {
        SocketReceiveFromResult result;

        try
        {
            result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, ct);
        }
        catch (OperationCanceledException) { break; }
        catch (SocketException ex)
        {
            Console.WriteLine($"[dengar] galat terima: {ex.SocketErrorCode}");
            continue;
        }

        count++;
        var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(result.ReceivedBytes, 60));

        // Sepuluh pertama dicetak seluruhnya, sesudahnya dijarangkan: yang perlu
        // dibuktikan adalah "ada yang sampai", bukan setiap paketnya.
        if (count <= 10 || count % 50 == 0)
        {
            Console.WriteLine($"[dengar] #{count} {result.ReceivedBytes} byte "
                            + $"dari {result.RemoteEndPoint}: {text}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"[dengar] TOTAL DITERIMA: {count} datagram");
}

static async Task Send(CancellationToken ct, int delayMs)
{
    if (delayMs > 0) await Task.Delay(delayMs, ct);

    var group = new IPEndPoint(IPAddress.Parse(Group), Port);
    var senders = new List<(IPAddress Address, Socket Socket)>();

    foreach (var local in MulticastAddresses())
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            senders.Add((local, socket));
            Console.WriteLine($"[kirim] soket siap di {local}");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[kirim] GAGAL menyiapkan {local}: {ex.SocketErrorCode}");
        }
    }

    if (senders.Count == 0)
    {
        Console.WriteLine("[kirim] tidak ada antarmuka yang dapat dipakai. Berhenti.");
        return;
    }

    Console.WriteLine();

    for (var i = 1; i <= 20 && !ct.IsCancellationRequested; i++)
    {
        foreach (var (address, socket) in senders)
        {
            var payload = Encoding.UTF8.GetBytes($"netcheck #{i} dari {address}");

            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, group, ct);
                Console.WriteLine($"[kirim] #{i} lewat {address} — {payload.Length} byte");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[kirim] #{i} lewat {address} GAGAL: {ex.SocketErrorCode}");
            }
        }

        try { await Task.Delay(500, ct); }
        catch (OperationCanceledException) { break; }
    }

    foreach (var (_, socket) in senders) socket.Dispose();

    Console.WriteLine();
    Console.WriteLine("[kirim] selesai.");
}
