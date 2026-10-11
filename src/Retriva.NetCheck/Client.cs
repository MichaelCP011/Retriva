using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Retriva.NetCheck;

public enum Verdict { Pass, Warn, Fail, Skip }

/// <summary>Satu baris laporan: apa yang diuji, hasilnya, dan apa artinya.</summary>
public sealed record Finding(string Title, Verdict Verdict, string Detail, string? Remedy = null);

/// <summary>
/// Sisi PC mahasiswa: menjalankan seluruh rantai pemeriksaan, lalu memberi vonis.
///
/// MENCETAK KESIMPULAN, BUKAN DATA MENTAH — dan itu keputusan utama di berkas ini.
///
/// Versi sebelumnya mencetak angka: berapa dikirim, berapa diterima, antarmuka
/// apa saja. Angka itu benar dan tidak menolong. Orang yang membacanya di ruang
/// praktikum, di antara dua kelas, harus tahu lebih dulu berapa angka yang
/// WAJAR — dan kalau ia sudah tahu itu, ia tidak butuh alatnya.
///
/// Yang dibutuhkan adalah kalimat: "multicast keluar dari koordinator tetapi
/// tidak sampai di sini; switch kemungkinan memangkasnya". Itu menyebut
/// langkah berikutnya. Angka tetap dicetak, di bawah, untuk yang ingin
/// memeriksa sendiri.
///
/// URUTANNYA MENIRU URUTAN KEGAGALAN, bukan urutan lapisan OSI. Yang diperiksa
/// lebih dulu adalah yang bila gagal membuat sisanya tidak berarti: tidak ada
/// gunanya melaporkan multicast gagal pada mesin yang bahkan tidak dapat
/// menjangkau koordinatornya.
/// </summary>
public static class Client
{
    public static async Task<int> RunAsync(string host, int retrivaPort, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var notes = new List<string>();

        Console.WriteLine("Retriva NetCheck — sisi PC MAHASISWA");
        Console.WriteLine($"Koordinator yang diuji: {host}");
        Console.WriteLine();
        Console.WriteLine("Menjalankan pemeriksaan. Sekitar dua setengah menit.");
        Console.WriteLine();

        /* ---------------- 1. antarmuka lokal ---------------- */

        var nics = Net.Interfaces();
        findings.Add(CheckInterfaces(nics, notes));

        /* ---------------- 2. saluran kendali ---------------- */

        var control = await Net.ProbeTcp(host, Net.ControlPort, 4000);

        if (!control.Ok)
        {
            findings.Add(new Finding(
                $"Saluran uji ke {host}:{Net.ControlPort}", Verdict.Fail, control.Detail,
                "Jalankan \"RetrivaNetCheck server\" di PC Asisten lebih dulu, dan pastikan "
              + "alamat yang diketik di sini adalah alamat yang dicetak jendela itu."));

            Report(findings, notes);
            return 2;
        }

        findings.Add(new Finding(
            $"Saluran uji ke {host}:{Net.ControlPort}", Verdict.Pass, $"{control.Ms} ms"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, Net.ControlPort, ct);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        await writer.WriteLineAsync("HELLO");
        var hello = await reader.ReadLineAsync(ct) ?? "";
        notes.Add($"Koordinator: {hello.Replace("HELLO 1 ", "")}");

        /* ---------------- 3. porta Retriva ---------------- */

        var retriva = await Net.ProbeTcp(host, retrivaPort, 4000);

        findings.Add(new Finding(
            $"Koordinator Retriva di {host}:{retrivaPort}",
            retriva.Ok ? Verdict.Pass : Verdict.Fail,
            retriva.Ok ? $"{retriva.Ms} ms" : retriva.Detail,
            retriva.Ok ? null
                : "Saluran uji tembus tetapi porta Retriva tidak. Berarti jaringannya baik "
                + "dan yang bermasalah adalah Retriva sendiri: koordinator belum berjalan, "
                + "atau aturan firewall TCP 8443 belum ada di PC Asisten."));

        /* ---------------- 4. sidik jari sertifikat ---------------- */

        findings.Add(retriva.Ok
            ? await CheckCertificate(host, retrivaPort, notes, ct)
            : new Finding("Sertifikat koordinator", Verdict.Skip, "porta tidak terjangkau"));

        /* ---------------- 5. firewall lokal ---------------- */

        findings.Add(CheckFirewall());

        /* ---------------- 6. multicast koordinator -> sini ---------------- */

        var socket = Net.OpenReceiver(Net.BroadcastPort, out var joined, out var refusedJoin);

        if (joined.Count == 0)
        {
            findings.Add(new Finding("Bergabung ke grup multicast", Verdict.Fail,
                $"tidak satu antarmuka pun berhasil ({string.Join(", ", refusedJoin)})",
                "Tanpa keanggotaan grup, siaran tidak akan pernah diterima mesin ini."));

            Report(findings, notes);
            return 2;
        }

        notes.Add($"Bergabung ke grup lewat: {string.Join(", ", joined)}");

        var seen = new List<(DateTime At, int Seq)>();
        var gate = new Lock();
        using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = ReceiveLoop(socket, seen, gate, receiveStop.Token);

        // Jeda supaya keanggotaan grup benar-benar tercatat di switch sebelum
        // datagram pertama lewat. Switch dengan IGMP snooping butuh melihat
        // laporan keanggotaan; mengirim lebih cepat dari itu akan kehilangan
        // paket awal dan terbaca sebagai kehilangan jaringan.
        await Task.Delay(1500, ct);

        Console.WriteLine("  [1/3] Menguji multicast koordinator -> PC ini ...");

        lock (gate) seen.Clear();
        await writer.WriteLineAsync("BURST 200 2");
        var burstReply = await reader.ReadLineAsync(ct) ?? "SENT 0";
        var sent = ParseNumber(burstReply);

        await Task.Delay(1500, ct);

        int got;
        lock (gate) got = seen.Select(s => s.Seq).Distinct().Count();

        findings.Add(JudgeDelivery("Multicast koordinator -> PC ini", sent, got, nics));

        /* ---------------- 7. multicast sini -> koordinator ---------------- */

        Console.WriteLine("  [2/3] Menguji multicast PC ini -> koordinator ...");

        await writer.WriteLineAsync("MARK");
        await reader.ReadLineAsync(ct);

        var pushed = await SendBurst(200, "c2s", ct);
        await Task.Delay(1500, ct);

        await writer.WriteLineAsync("COUNT");
        var countReply = await reader.ReadLineAsync(ct) ?? "GOT 0";
        var arrived = ParseNumber(countReply);

        findings.Add(JudgeDelivery("Multicast PC ini -> koordinator", pushed, arrived, nics));

        /* ---------------- 8. ketahanan (IGMP querier) ---------------- */

        Console.WriteLine("  [3/3] Menguji ketahanan multicast selama 90 detik ...");
        Console.WriteLine("        (switch tertentu memangkas multicast setelah sekitar satu menit)");

        lock (gate) seen.Clear();
        await writer.WriteLineAsync("SUSTAIN 90 5");
        await reader.ReadLineAsync(ct);          // READY

        var started = DateTime.UtcNow;
        for (var elapsed = 0; elapsed < 95; elapsed += 5)
        {
            await Task.Delay(5000, ct);
            int soFar;
            lock (gate) soFar = seen.Count;
            Console.Write($"\r        {elapsed + 5,3} detik — {soFar} datagram diterima   ");
        }
        Console.WriteLine();

        await reader.ReadLineAsync(ct);          // SENT n

        List<(DateTime At, int Seq)> timeline;
        lock (gate) timeline = [.. seen];

        findings.Add(JudgeSustain(timeline, started));

        receiveStop.Cancel();
        try { await pump; } catch (OperationCanceledException) { }
        socket.Dispose();

        await writer.WriteLineAsync("BYE");

        Report(findings, notes);
        return findings.Any(f => f.Verdict == Verdict.Fail) ? 2 : 0;
    }

    /* ================= pemeriksaan satuan ================= */

    private static Finding CheckInterfaces(List<Nic> nics, List<string> notes)
    {
        foreach (var n in nics)
        {
            var tags = new List<string>();
            if (n.IsApipa) tags.Add("APIPA");
            if (n.IsVirtual) tags.Add("virtual");
            if (n.IsWireless) tags.Add("nirkabel");
            if (!n.SupportsMulticast) tags.Add("tanpa multicast");

            notes.Add($"  {n.Address,-16} {n.Name}"
                    + (n.SpeedMbps > 0 ? $"  {n.SpeedMbps} Mbps" : "")
                    + (tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : ""));
        }

        if (nics.Count == 0)
            return new Finding("Antarmuka jaringan", Verdict.Fail, "tidak ada antarmuka IPv4 aktif");

        if (nics.All(n => n.IsApipa))
            return new Finding("Antarmuka jaringan", Verdict.Fail,
                "semua alamat 169.254.x — DHCP gagal",
                "Mesin ini tidak mendapat alamat dari router. Periksa kabel dan DHCP; "
              + "tidak ada perubahan pada Retriva yang dapat memperbaikinya.");

        // Nirkabel disebut sebagai PERINGATAN, bukan kegagalan. Banyak titik
        // akses membuang atau memperlambat multicast secara diam-diam, dan itu
        // penyebab yang tidak akan pernah terlihat dari sisi kode.
        if (nics.Any(n => n.IsWireless && !n.IsVirtual))
            return new Finding("Antarmuka jaringan", Verdict.Warn,
                $"{nics.Count} antarmuka, salah satunya nirkabel",
                "Titik akses Wi-Fi sering membuang atau memperlambat multicast. "
              + "Bila siaran tersendat, uji dengan kabel sebelum menyalahkan kode.");

        var real = nics.Count(n => !n.IsVirtual && !n.IsApipa);

        return real > 1
            ? new Finding("Antarmuka jaringan", Verdict.Warn,
                $"{real} antarmuka nyata — pilihan rute bisa tidak terduga")
            : new Finding("Antarmuka jaringan", Verdict.Pass, $"{nics.Count} antarmuka");
    }

    private static async Task<Finding> CheckCertificate(
        string host, int port, List<string> notes, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);

            // Sertifikat DITERIMA apa adanya di sini, dan itu bukan kelalaian.
            // Alat ini bertugas MELAPORKAN sidik jari yang disajikan, bukan
            // mempercayainya. Yang membandingkannya dengan nilai yang benar
            // adalah orang yang membaca laporan — dan Retriva sendiri, yang
            // penyematannya tidak disentuh alat ini.
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(host);

            if (ssl.RemoteCertificate is not { } cert)
                return new Finding("Sertifikat koordinator", Verdict.Warn, "tidak disajikan");

            var thumb = Convert.ToHexString(
                SHA256.HashData(new X509Certificate2(cert).RawData)).ToLowerInvariant();

            notes.Add($"Sidik jari SHA-256: {thumb}");

            return new Finding("Sertifikat koordinator", Verdict.Pass,
                $"TLS terbentuk, sidik {thumb[..16]}…");
        }
        catch (Exception ex)
        {
            return new Finding("Sertifikat koordinator", Verdict.Fail, ex.Message,
                "Porta terbuka tetapi TLS gagal. Koordinator mungkin baru dijalankan, "
              + "atau sertifikatnya rusak.");
        }
    }

    /// <summary>
    /// Memeriksa aturan firewall masuk untuk porta Retriva.
    ///
    /// HANYA DI WINDOWS, dan dilewati di tempat lain alih-alih ditebak. Alat ini
    /// berjalan di Linux juga — VM Ubuntu dapat dipakai sebagai node kedua tanpa
    /// lisensi Windows — dan "netsh tidak ditemukan" bukan temuan yang berguna.
    /// </summary>
    private static Finding CheckFirewall()
    {
        if (!OperatingSystem.IsWindows())
            return new Finding("Aturan firewall masuk", Verdict.Skip, "bukan Windows");

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("netsh",
                    "advfirewall firewall show rule name=all dir=in")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

            if (process is null)
                return new Finding("Aturan firewall masuk", Verdict.Warn, "netsh tidak dapat dijalankan");

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);

            var hasRetriva = output.Contains("Retriva", StringComparison.OrdinalIgnoreCase);

            return hasRetriva
                ? new Finding("Aturan firewall masuk", Verdict.Pass, "aturan Retriva ada")
                : new Finding("Aturan firewall masuk", Verdict.Warn,
                    "tidak ada aturan bernama Retriva",
                    "Installer biasanya membuatnya. Ketiadaannya berarti Retriva dipasang "
                  + "dengan cara lain, atau aturannya terhapus. Siaran dan kendali jarak "
                  + "jauh akan diblokir masuk.");
        }
        catch (Exception ex)
        {
            return new Finding("Aturan firewall masuk", Verdict.Warn, ex.Message);
        }
    }

    /// <summary>
    /// Menilai pengiriman satu arah.
    ///
    /// Kehilangan sebagian dan kehilangan total DIBEDAKAN, karena keduanya
    /// menunjuk ke tempat yang berbeda. Nol berarti jalurnya tertutup —
    /// firewall, switch, atau keanggotaan grup. Sebagian berarti jalurnya
    /// terbuka tetapi tidak sanggup — buffer meluap, tautan jenuh, atau
    /// nirkabel.
    /// </summary>
    private static Finding JudgeDelivery(string title, int sent, int got, List<Nic> nics)
    {
        if (sent == 0)
            return new Finding(title, Verdict.Fail, "pengirim tidak memancarkan apa pun",
                "Tidak ada antarmuka yang dapat dipakai memancarkan multicast di sisi pengirim.");

        // Pengiriman dilakukan lewat setiap antarmuka, jadi satu datagram logis
        // dapat tiba beberapa kali. Yang dihitung nomor urut unik, dan patokannya
        // jumlah logis — bukan jumlah tulisan ke soket.
        var expected = Math.Max(1, sent / Math.Max(1, nics.Count(n => n.SupportsMulticast && !n.IsApipa)));
        var ratio = (double)got / expected;

        if (got == 0)
            return new Finding(title, Verdict.Fail, $"0 dari ~{expected} datagram",
                "Pengirim memancarkan, penerima tidak menerima apa pun. Tersangka, "
              + "berurutan: profil jaringan Public di salah satu mesin (multicast masuk "
              + "diblokir), aturan firewall UDP 9100-9103 tidak ada, atau switch memangkas "
              + "multicast. Periksa profil jaringan lebih dulu — itu yang paling sering.");

        if (ratio < 0.5)
            return new Finding(title, Verdict.Fail,
                $"{got} dari ~{expected} datagram ({ratio:P0})",
                "Sebagian besar hilang. Jalurnya terbuka tetapi tidak andal — tautan "
              + "nirkabel, switch yang membatasi laju multicast, atau tautan jenuh.");

        if (ratio < 0.95)
            return new Finding(title, Verdict.Warn,
                $"{got} dari ~{expected} datagram ({ratio:P0})",
                "Ada kehilangan. Siaran akan terlihat tersendat, dan sebagian frame "
              + "tidak pernah lengkap.");

        return new Finding(title, Verdict.Pass, $"{got} dari ~{expected} datagram");
    }

    /// <summary>
    /// Menilai apakah multicast BERTAHAN, bukan sekadar sempat sampai.
    ///
    /// INI PEMERIKSAAN YANG PALING SULIT DITEMUKAN DENGAN MENEBAK.
    ///
    /// Switch terkelola yang menjalankan IGMP snooping tanpa ada IGMP querier di
    /// jaringan akan meneruskan multicast pada awalnya, lalu memangkasnya
    /// setelah tabel keanggotaannya kedaluwarsa — biasanya sekitar satu menit.
    ///
    /// Gejalanya persis seperti bug yang tidak dapat direproduksi: siaran
    /// berjalan sempurna saat diuji sebentar, lalu mati di tengah kelas. Tidak
    /// ada satu pun galat di kedua sisi, dan pengujian ulang berikutnya berhasil
    /// lagi karena keanggotaannya baru diperbarui.
    ///
    /// Membaginya per sepuluh detik membuat pola itu terlihat sebagai angka.
    /// </summary>
    private static Finding JudgeSustain(List<(DateTime At, int Seq)> timeline, DateTime started)
    {
        if (timeline.Count == 0)
            return new Finding("Ketahanan multicast (90 detik)", Verdict.Fail,
                "tidak ada datagram sama sekali",
                "Sudah gagal sejak awal — lihat temuan multicast di atas.");

        var buckets = new int[9];
        foreach (var (at, _) in timeline)
        {
            var index = (int)((at - started).TotalSeconds / 10);
            if (index is >= 0 and < 9) buckets[index]++;
        }

        var pattern = string.Join(" ", buckets.Select(b => b.ToString().PadLeft(3)));

        var early = buckets.Take(3).Sum();
        var late = buckets.Skip(6).Sum();

        // Berhenti total di paruh akhir setelah lancar di awal adalah tanda khas
        // pemangkasan oleh switch, bukan gangguan acak.
        if (early > 0 && late == 0)
            return new Finding("Ketahanan multicast (90 detik)", Verdict.Fail,
                $"berhenti di tengah — per 10 detik: {pattern}",
                "Multicast mengalir di awal lalu berhenti. Ini pola khas switch dengan "
              + "IGMP snooping tanpa IGMP querier di jaringan: tabel keanggotaannya "
              + "kedaluwarsa dan multicast dipangkas.\n"
              + "      Perbaikannya di switch, bukan di Retriva — nyalakan IGMP querier, "
              + "atau matikan IGMP snooping pada VLAN lab.\n"
              + "      Inilah sebabnya siaran berjalan saat diuji sebentar lalu mati di "
              + "tengah kelas.");

        if (late < early / 2)
            return new Finding("Ketahanan multicast (90 detik)", Verdict.Warn,
                $"menurun di akhir — per 10 detik: {pattern}",
                "Aliran menyusut seiring waktu. Tersangka yang sama dengan di atas, "
              + "atau titik akses nirkabel yang membatasi laju.");

        return new Finding("Ketahanan multicast (90 detik)", Verdict.Pass,
            $"{timeline.Count} datagram, stabil — per 10 detik: {pattern}");
    }

    /* ================= pembantu ================= */

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

            if (Net.ReadSeq(buffer.AsSpan(0, result.ReceivedBytes), "s2c") is { } seq)
                lock (gate) seen.Add((DateTime.UtcNow, seq));
        }
    }

    private static async Task<int> SendBurst(int count, string tag, CancellationToken ct)
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

            await Task.Delay(2, ct);
        }

        foreach (var (_, socket) in senders) socket.Dispose();
        return sent;
    }

    private static int ParseNumber(string line)
    {
        var parts = line.Split(' ');
        return parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
    }

    private static void Report(List<Finding> findings, List<string> notes)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine("HASIL");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        foreach (var f in findings)
        {
            var mark = f.Verdict switch
            {
                Verdict.Pass => "LULUS ",
                Verdict.Warn => "AWAS  ",
                Verdict.Fail => "GAGAL ",
                _ => "-     ",
            };

            var dots = new string('.', Math.Max(2, 44 - f.Title.Length));
            Console.WriteLine($"[{mark}] {f.Title} {dots} {f.Detail}");

            if (f.Remedy is not null)
            {
                foreach (var line in f.Remedy.Split('\n'))
                    Console.WriteLine($"      -> {line.TrimStart()}");
                Console.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 74));
        Console.WriteLine("Rincian");
        Console.WriteLine(new string('-', 74));
        foreach (var n in notes) Console.WriteLine(n);
        Console.WriteLine();

        var failed = findings.Count(f => f.Verdict == Verdict.Fail);
        var warned = findings.Count(f => f.Verdict == Verdict.Warn);

        Console.WriteLine(failed > 0
            ? $"{failed} pemeriksaan GAGAL, {warned} perlu diperhatikan."
            : warned > 0
                ? $"Semua lulus, {warned} perlu diperhatikan."
                : "Semua lulus. Jaringan bukan penyebabnya — periksa sisi Retriva.");

        Console.WriteLine();
    }
}
