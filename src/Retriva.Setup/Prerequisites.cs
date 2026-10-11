using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Retriva.Setup;

/// <summary>Hasil satu pemeriksaan prasyarat.</summary>
public enum CheckState
{
    /// <summary>Terpenuhi. Pemasangan dapat lanjut.</summary>
    Pass,

    /// <summary>Tidak terpenuhi tetapi tidak menghalangi. Perlu diketahui, bukan diperbaiki sekarang.</summary>
    Warn,

    /// <summary>Menghalangi. Pemasangan akan gagal atau menghasilkan unit yang tidak berfungsi.</summary>
    Fail,

    /// <summary>
    /// TIDAK DAPAT DIPERIKSA dari sini.
    ///
    /// Keadaan keempat ini ada karena tiga keadaan tidak cukup. Wake-on-LAN
    /// diatur di BIOS dan tidak selalu terbaca dari Windows; autologon bisa
    /// diatur lewat beberapa mekanisme yang berbeda. Memaksa jawaban ke dalam
    /// Pass atau Fail berarti installer mengaku tahu sesuatu yang tidak
    /// diketahuinya — dan orang yang membacanya akan berhenti memeriksa sendiri.
    /// </summary>
    Unknown,
}

public sealed record Check(string Title, CheckState State, string Detail);

/// <summary>
/// Pemeriksaan prasyarat sebelum agent dipasang.
///
/// SETIAP PEMERIKSAAN DI SINI BENAR-BENAR DIJALANKAN.
///
/// Mockup installer menampilkan daftar prasyarat yang meyakinkan — "balasan
/// 3 ms", "42 GB tersedia", "Quick Sync tidak terdeteksi". Angka-angka itu
/// ditulis di dalam mockup sebagai teks. Daftar prasyarat yang isinya dikarang
/// adalah bentuk terburuk dari fitur ini: ia memberi keyakinan tepat pada
/// momen ketika orangnya paling membutuhkan kebenaran, lalu pemasangannya gagal
/// beberapa menit kemudian karena hal yang katanya sudah diperiksa.
///
/// Jadi yang tidak dapat diukur dilaporkan sebagai Unknown, bukan sebagai Pass.
/// </summary>
public static class Prerequisites
{
    public static List<Check> RunAll(string coordinatorHost, int coordinatorPort)
    {
        return
        [
            Administrator(),
            Architecture(),
            WindowsVersion(),
            DiskSpace(),
            CoordinatorReachable(coordinatorHost, coordinatorPort),
            WakeOnLan(),
            ExistingInstall(),
        ];
    }

    private static Check Administrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            return new Check("Hak administrator",
                elevated ? CheckState.Pass : CheckState.Fail,
                elevated
                    ? "diberikan"
                    : "belum — layanan Windows tidak dapat didaftarkan tanpa ini");
        }
        catch (Exception ex)
        {
            return new Check("Hak administrator", CheckState.Unknown, ex.Message);
        }
    }

    private static Check Architecture()
    {
        var arch = RuntimeInformation.OSArchitecture;
        var ok = arch is System.Runtime.InteropServices.Architecture.X64;

        return new Check("Arsitektur x64",
            ok ? CheckState.Pass : CheckState.Fail,
            ok ? "terpenuhi" : $"terdeteksi {arch} — build ini hanya untuk x64");
    }

    private static Check WindowsVersion()
    {
        var v = Environment.OSVersion.Version;

        /*
            Build 22000 adalah Windows 11. Angka ini yang diperiksa, BUKAN
            "versi 22H2" seperti di mockup.

            Nama pemasaran seperti 22H2 tidak muncul di Environment.OSVersion;
            yang ada hanya nomor build. Menuliskan "22H2" di layar sementara yang
            diperiksa adalah angka lain berarti pesan gagalnya menyuruh orang
            mencari hal yang berbeda dari yang sebenarnya kurang.

            Windows 10 diberi peringatan, bukan penolakan: WebView2 dan .NET 10
            berjalan di sana, dan lab yang belum diperbarui tetap dapat memakai
            Retriva. Yang mungkin berbeda hanya beberapa perilaku jendela.
        */
        if (v.Build >= 22000)
            return new Check("Windows 11", CheckState.Pass, $"build {v.Build}");

        if (v.Major >= 10)
            return new Check("Windows 11", CheckState.Warn,
                $"terdeteksi Windows 10 build {v.Build} — berjalan, tetapi belum diuji selengkap Windows 11");

        return new Check("Windows 11", CheckState.Fail, $"versi {v} terlalu lama");
    }

    private static Check DiskSpace()
    {
        // Diukur pada drive tempat Program Files berada, bukan pada C: yang
        // diandaikan. Keduanya biasanya sama, dan "biasanya" bukan dasar yang
        // cukup untuk sebuah pemeriksaan.
        try
        {
            var target = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var drive = new DriveInfo(Path.GetPathRoot(target) ?? "C:\\");
            var freeMb = drive.AvailableFreeSpace / 1024 / 1024;

            const long neededMb = 350;

            return new Check("Ruang penyimpanan",
                freeMb >= neededMb ? CheckState.Pass : CheckState.Fail,
                $"{freeMb:N0} MB tersedia di {drive.Name} · dibutuhkan sekitar {neededMb} MB");
        }
        catch (Exception ex)
        {
            return new Check("Ruang penyimpanan", CheckState.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Menyambung ke koordinator, dan MENGUKUR waktunya.
    ///
    /// Bukan ping ICMP. Yang perlu diketahui adalah apakah porta TLS koordinator
    /// dapat dijangkau — dan ICMP dapat lolos di jaringan yang memblokir 8443,
    /// sehingga pemeriksaan yang memakai ping akan berhasil pada mesin yang
    /// nanti gagal mendaftar.
    /// </summary>
    private static Check CoordinatorReachable(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new Check($"Koneksi ke koordinator", CheckState.Unknown,
                "alamat koordinator belum diisi");

        var title = $"Koneksi ke {host}:{port}";
        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            var connecting = client.ConnectAsync(host, port);

            if (!connecting.Wait(TimeSpan.FromSeconds(3)))
                return new Check(title, CheckState.Fail,
                    "tidak menjawab dalam 3 detik — periksa alamat, kabel, dan firewall koordinator");

            // Wait yang selesai tidak berarti berhasil: tugas yang gagal juga
            // selesai. Pengecualiannya dilempar di sini oleh Wait dan tertangkap
            // di bawah, tetapi memeriksa Connected membuatnya tegas.
            return client.Connected
                ? new Check(title, CheckState.Pass, $"balasan {watch.ElapsedMilliseconds} ms")
                : new Check(title, CheckState.Fail, "sambungan tidak terbentuk");
        }
        catch (Exception ex)
        {
            var reason = ex is AggregateException agg && agg.InnerException is { } inner
                ? inner.Message
                : ex.Message;

            return new Check(title, CheckState.Fail, reason);
        }
    }

    /// <summary>
    /// Wake-on-LAN: dilaporkan Unknown bila tidak dapat dibaca, bukan Fail.
    ///
    /// Yang menentukan WoL benar-benar bekerja ada di BIOS dan di properti
    /// driver adapter, dan tidak seluruhnya terbaca dari kode terkelola. Yang
    /// dapat diperiksa dari sini hanya apakah ada adapter Ethernet — karena WoL
    /// lewat Wi-Fi hampir selalu tidak bekerja, dan itu saja sudah menjelaskan
    /// sebagian besar kasus PC yang tidak mau menyala.
    /// </summary>
    private static Check WakeOnLan()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var wired = nics.Any(n => n.NetworkInterfaceType
                is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet);

            if (!wired)
                return new Check("Wake-on-LAN", CheckState.Warn,
                    "tidak ada adapter Ethernet aktif — Wake-on-LAN lewat Wi-Fi hampir selalu tidak bekerja");

            return new Check("Wake-on-LAN", CheckState.Unknown,
                "adapter Ethernet ada · aktifkan 'Wake on Magic Packet' di BIOS dan properti adapter — "
              + "tidak dapat diperiksa dari sini");
        }
        catch (Exception ex)
        {
            return new Check("Wake-on-LAN", CheckState.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Pemasangan sebelumnya, beserta token perangkatnya.
    ///
    /// PENTING SUPAYA TOKEN TIDAK HILANG. Token perangkat adalah satu-satunya
    /// bukti bahwa unit ini pernah disetujui; ia tersimpan di agent.json di
    /// ProgramData, di luar Program Files. Pemasangan ulang yang menghapusnya
    /// membuat unit mendaftar dari nol dan kehilangan namanya beserta seluruh
    /// riwayatnya — tanpa satu pun galat.
    /// </summary>
    private static Check ExistingInstall()
    {
        try
        {
            var config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Retriva", "agent.json");

            if (!File.Exists(config))
                return new Check("Pemasangan sebelumnya", CheckState.Pass, "belum ada — pemasangan baru");

            var hasToken = File.ReadAllText(config).Contains("\"deviceToken\"",
                StringComparison.OrdinalIgnoreCase);

            return new Check("Pemasangan sebelumnya", CheckState.Warn, hasToken
                ? "ditemukan beserta token perangkat · token dipertahankan, unit tidak perlu disetujui ulang"
                : "ditemukan tanpa token · unit akan mendaftar sebagai unit baru");
        }
        catch (Exception ex)
        {
            return new Check("Pemasangan sebelumnya", CheckState.Unknown, ex.Message);
        }
    }
}
