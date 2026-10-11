using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Retriva.NetCheck;

/// <summary>
/// Sisi koordinator: menuruti perintah dari sisi klien.
///
/// SALURAN KENDALI LEWAT TCP, BUKAN KESEPAKATAN WAKTU.
///
/// Versi pertama alat ini mengharuskan dua orang menjalankan dua perintah di
/// dua ruangan pada waktu yang kira-kira sama, lalu membandingkan angka di dua
/// layar lewat telepon. Cara itu bekerja, tetapi setiap pengujian menuntut dua
/// orang, dan hasil "0 diterima" selalu menyisakan pertanyaan yang tidak dapat
/// dijawab: apakah pengirimnya memang belum mulai?
///
/// Saluran TCP menghapus keraguan itu. Klien menyuruh, koordinator menjawab
/// berapa yang benar-benar ia tulis ke soket, dan selisih antara "ditulis" dan
/// "diterima" menjadi angka yang berarti — bukan dugaan tentang penekanan tombol
/// yang tidak bersamaan.
///
/// Efek sampingnya penting: pengujian dapat dijalankan SATU ORANG. Nyalakan
/// mode ini di PC Asisten, lalu berjalan ke PC mahasiswa dan jalankan sisi
/// kliennya. Tidak ada yang perlu ditekan bersamaan.
/// </summary>
public static class Server
{
    public static async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, Net.ControlPort);
        listener.Start();

        Console.WriteLine($"Retriva NetCheck — sisi KOORDINATOR");
        Console.WriteLine($"Mendengarkan perintah di TCP {Net.ControlPort}");
        Console.WriteLine();

        var nics = Net.Interfaces();
        Console.WriteLine("Alamat mesin ini:");
        foreach (var n in nics)
        {
            var tags = new List<string>();
            if (n.IsApipa) tags.Add("APIPA — DHCP GAGAL");
            if (n.IsVirtual) tags.Add("virtual");
            if (n.IsWireless) tags.Add("nirkabel");
            if (!n.SupportsMulticast) tags.Add("TANPA MULTICAST");

            Console.WriteLine($"  {n.Address,-16} {n.Name}"
                            + (tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : ""));
        }

        Console.WriteLine();
        Console.WriteLine("Di PC mahasiswa, jalankan:");
        foreach (var n in nics.Where(x => !x.IsApipa && !x.IsVirtual))
            Console.WriteLine($"  RetrivaNetCheck client --host {n.Address}");

        Console.WriteLine();
        Console.WriteLine("Biarkan jendela ini terbuka. Ctrl+C untuk berhenti.");
        Console.WriteLine();

        // Penerima dinyalakan sepanjang sesi, bukan saat diminta. Bergabung ke
        // grup multicast butuh waktu di switch yang menjalankan IGMP snooping,
        // dan bergabung tepat sebelum menghitung akan kehilangan paket pertama
        // — yang terbaca sebagai kehilangan jaringan, padahal keterlambatan
        // kita sendiri.
        var socket = Net.OpenReceiver(Net.BroadcastPort, out var joined, out var refused);
        var seen = new List<(DateTime At, int Seq)>();
        var seenGate = new Lock();

        Console.WriteLine($"Bergabung ke grup lewat: {string.Join(", ", joined)}");
        if (refused.Count > 0) Console.WriteLine($"Ditolak: {string.Join(", ", refused)}");
        Console.WriteLine();

        _ = ReceiveLoop(socket, seen, seenGate, ct);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }

            var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Klien tersambung dari {from}");

            // Satu klien pada satu waktu. Dua pengujian yang berjalan bersamaan
            // akan saling menghitung paket lawannya, dan hasilnya lebih buruk
            // daripada tidak ada hasil: ia terlihat benar.
            try { await Serve(client, seen, seenGate, ct); }
            catch (Exception ex) { Console.WriteLine($"    sesi berakhir: {ex.Message}"); }
            finally { client.Dispose(); }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Klien selesai.");
            Console.WriteLine();
        }

        socket.Dispose();
        listener.Stop();
    }

    private static async Task ReceiveLoop(
        Socket socket, List<(DateTime, int)> seen, Lock gate, CancellationToken ct)
    {
        var buffer = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try { result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            if (Net.ReadSeq(buffer.AsSpan(0, result.ReceivedBytes), "c2s") is { } seq)
                lock (gate) seen.Add((DateTime.UtcNow, seq));
        }
    }

    private static async Task Serve(
        TcpClient client, List<(DateTime At, int Seq)> seen, Lock gate, CancellationToken ct)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            var parts = line.Split(' ');
            var verb = parts[0].ToUpperInvariant();

            switch (verb)
            {
                case "HELLO":
                {
                    var ips = string.Join(",", Net.Interfaces()
                        .Where(n => !n.IsApipa)
                        .Select(n => n.Address));

                    await writer.WriteLineAsync($"HELLO 1 {Environment.MachineName} {ips}");
                    break;
                }

                // Memancarkan sejumlah datagram, lalu melaporkan berapa yang
                // BENAR-BENAR ditulis ke soket. Angka itu yang dibandingkan
                // klien dengan jumlah yang sampai kepadanya.
                case "BURST":
                {
                    var count = Parse(parts, 1, 200);
                    var gapMs = Parse(parts, 2, 2);
                    var sent = await Burst(count, gapMs, "s2c", ct);
                    Console.WriteLine($"    BURST {count} -> {sent} datagram dipancarkan");
                    await writer.WriteLineAsync($"SENT {sent}");
                    break;
                }

                // Memancarkan pelan dalam waktu lama. Dipakai menguji apakah
                // switch memangkas multicast di tengah jalan.
                case "SUSTAIN":
                {
                    var seconds = Parse(parts, 1, 90);
                    var perSecond = Parse(parts, 2, 5);

                    Console.WriteLine($"    SUSTAIN {seconds} detik, {perSecond}/detik");
                    await writer.WriteLineAsync("READY");

                    var sent = 0;
                    var stop = DateTime.UtcNow.AddSeconds(seconds);
                    var senders = Net.OpenSenders(out _);
                    var group = new IPEndPoint(IPAddress.Parse(Net.Group), Net.BroadcastPort);
                    var seq = 0;

                    while (DateTime.UtcNow < stop && !ct.IsCancellationRequested)
                    {
                        foreach (var (_, s) in senders)
                        {
                            try
                            {
                                await s.SendToAsync(
                                    Net.Payload(seq, "s2c", Net.PacketSize), SocketFlags.None, group, ct);
                                sent++;
                            }
                            catch (SocketException) { }
                        }

                        seq++;
                        try { await Task.Delay(1000 / Math.Max(1, perSecond), ct); }
                        catch (OperationCanceledException) { break; }
                    }

                    foreach (var (_, s) in senders) s.Dispose();
                    Console.WriteLine($"    SUSTAIN selesai — {sent} dipancarkan");
                    await writer.WriteLineAsync($"SENT {sent}");
                    break;
                }

                // Menandai titik awal hitungan untuk arah klien -> koordinator.
                case "MARK":
                {
                    lock (gate) seen.Clear();
                    await writer.WriteLineAsync("MARKED");
                    break;
                }

                case "COUNT":
                {
                    int n;
                    lock (gate) n = seen.Count;
                    Console.WriteLine($"    COUNT -> {n} datagram diterima dari klien");
                    await writer.WriteLineAsync($"GOT {n}");
                    break;
                }

                case "BYE":
                    return;

                default:
                    await writer.WriteLineAsync("ERR perintah tidak dikenali");
                    break;
            }
        }
    }

    private static async Task<int> Burst(int count, int gapMs, string tag, CancellationToken ct)
    {
        var senders = Net.OpenSenders(out _);
        if (senders.Count == 0) return 0;

        var group = new IPEndPoint(IPAddress.Parse(Net.Group), Net.BroadcastPort);
        var sent = 0;

        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            foreach (var (_, socket) in senders)
            {
                try
                {
                    await socket.SendToAsync(
                        Net.Payload(i, tag, Net.PacketSize), SocketFlags.None, group, ct);
                    sent++;
                }
                catch (SocketException) { }
            }

            if (gapMs > 0) { try { await Task.Delay(gapMs, ct); } catch (OperationCanceledException) { break; } }
        }

        foreach (var (_, socket) in senders) socket.Dispose();
        return sent;
    }

    private static int Parse(string[] parts, int index, int fallback) =>
        parts.Length > index && int.TryParse(parts[index], out var v) ? v : fallback;
}
