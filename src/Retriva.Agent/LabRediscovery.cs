using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Retriva.Shared.Protocol;

namespace Retriva.Agent;

/// <summary>
/// Menemukan kembali koordinator yang alamatnya berubah, tanpa pemasangan ulang.
///
/// MASALAHNYA: ALAMAT ADALAH SATU-SATUNYA HAL YANG DITULIS SEKALI DAN TIDAK
/// PERNAH DIPERBARUI.
///
/// Alamat koordinator ditentukan saat pemasangan lalu tersimpan di agent.json
/// selamanya. Setiap hal yang wajar terjadi akan mematahkannya: DHCP memberi
/// PC Asisten alamat lain setelah pemadaman listrik, routernya diganti, subnet
/// lab ditata ulang, atau lab pindah ruangan.
///
/// Yang terjadi kemudian adalah empat puluh unit yang layanannya berjalan
/// sempurna, tidak melaporkan galat apa pun ke siapa pun, dan tidak satu pun
/// muncul di dashboard. Satu-satunya perbaikan yang tersedia sebelum ini adalah
/// memasang ulang keempat puluhnya.
///
/// YANG DIPERBAIKI DI SINI HANYA ALAMAT, BUKAN KEPERCAYAAN.
///
/// Beacon di jaringan tidak dapat dipercaya: siapa pun yang tersambung ke Wi-Fi
/// kampus dapat mengirim datagram yang mengaku sebagai lab, dan agent yang
/// mengikuti beacon mana pun akan menyerahkan layar, berkas, dan kendali
/// masukan mahasiswa kepada siapa pun yang meminta.
///
/// Karena itu beacon hanya diterima bila SIDIK JARI SERTIFIKATNYA SAMA PERSIS
/// dengan yang sudah disematkan agent ini. Sidik jari itu berasal dari
/// pemasangan, bukan dari jaringan, dan penyerang yang tidak memegang kunci
/// privat koordinator tidak dapat menyelesaikan jabat tangan TLS sekalipun ia
/// menyalin sidik jarinya ke dalam beacon palsu.
///
/// Jadi: alamat lab yang sama diperbaiki sendiri; berpindah ke lab yang BERBEDA
/// tetap menuntut kode pendaftaran lab itu, dan itu memang seharusnya.
/// </summary>
public static class LabRediscovery
{
    /// <summary>
    /// Mendengarkan beacon dan mengembalikan alamat lab yang sidik jarinya cocok.
    ///
    /// Mengembalikan null bila tidak ada yang terdengar, atau yang terdengar
    /// bukan lab yang dipercaya agent ini.
    /// </summary>
    public static string? FindTrusted(string expectedThumbprint, TimeSpan patience, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(expectedThumbprint))
        {
            // Tanpa sematan, tidak ada yang dapat dijadikan dasar penerimaan.
            // Agent memang sudah menolak menyambung dalam keadaan ini.
            return null;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var heard = 0;
        var rejected = 0;

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, PanelProtocol.DiscoveryPort));

            if (!JoinOnEveryInterface(socket, log)) return null;

            socket.ReceiveTimeout = 500;
            var buffer = new byte[8192];
            var deadline = DateTime.UtcNow + patience;

            while (DateTime.UtcNow < deadline)
            {
                int length;
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

                try { length = socket.ReceiveFrom(buffer, ref sender); }
                catch (SocketException) { continue; }   // batas waktu

                if (sender is not IPEndPoint from) continue;

                LabBeacon? beacon;
                try
                {
                    beacon = JsonSerializer.Deserialize<LabBeacon>(
                        Encoding.UTF8.GetString(buffer, 0, length),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { continue; }

                if (beacon is null || beacon.Port <= 0) continue;
                heard++;

                /*
                    Perbandingan sidik jari memakai OrdinalIgnoreCase.

                    Sidik jari ditulis sebagai heksadesimal, dan tidak ada
                    kesepakatan tunggal tentang huruf besar atau kecil di
                    antara alat yang menghasilkannya. Perbandingan yang peka
                    huruf akan menolak koordinator yang benar dengan alasan
                    yang tidak akan pernah terpikirkan siapa pun yang
                    membaca kedua nilainya berdampingan — keduanya terlihat
                    identik.
                */
                if (!string.Equals(beacon.Thumbprint?.Replace(":", "").Trim(),
                                   expectedThumbprint.Replace(":", "").Trim(),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    rejected++;
                    continue;
                }

                // Alamat PENGIRIM, bukan Host di dalam beacon. Koordinator
                // sengaja mengirimkannya kosong: mesin dengan beberapa kartu
                // jaringan tidak dapat mengetahui lewat mana ia terlihat.
                var host = from.Address.ToString();

                log.LogInformation(
                    "Koordinator {Lab} terdengar di {Host}:{Port} dengan sidik jari yang cocok",
                    beacon.LabName, host, beacon.Port);

                return $"https://{host}:{beacon.Port}";
            }
        }
        catch (SocketException ex)
        {
            log.LogWarning("Pencarian lab tidak dapat berjalan: {Message}", ex.Message);
            return null;
        }

        // Ketiga keadaan ini DIBEDAKAN di log, karena perbaikannya berbeda.
        if (heard == 0)
            log.LogInformation("Tidak ada beacon lab terdengar — koordinator mati, "
                             + "beda jaringan, atau multicast diblokir switch");
        else if (rejected > 0)
            log.LogWarning("{Count} lab terdengar, tetapi sidik jarinya tidak cocok. "
                         + "Unit ini terikat pada lab lain — pindah lab menuntut "
                         + "konfigurasi ulang dengan kode pendaftaran lab tujuan.", rejected);

        return null;
    }

    /// <summary>
    /// Bergabung lewat SETIAP antarmuka, bukan menyerahkannya ke tabel rute.
    ///
    /// Pelajaran yang sama dengan yang membuat relay siaran memancar ke semua
    /// antarmuka: pada mesin dengan VirtualBox atau VPN, pilihan bawaan sistem
    /// sering jatuh ke adapter virtual, dan beacon dari jaringan lab yang
    /// sebenarnya tidak pernah terdengar.
    /// </summary>
    private static bool JoinOnEveryInterface(Socket socket, ILogger log)
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

                // APIPA dilewati. 169.254.x berarti DHCP gagal, dan bergabung
                // lewat antarmuka itu tidak akan pernah mendengar apa pun —
                // persis kesalahan yang sempat membuat relay siaran memancar
                // ke tempat yang tidak ada siapa-siapa.
                if (address.Address.GetAddressBytes() is [169, 254, ..]) continue;

                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, address.Address));
                    joined++;
                }
                catch (SocketException) { /* antarmuka ini menolak; yang lain mungkin tidak */ }
            }
        }

        if (joined == 0) log.LogWarning("Tidak ada antarmuka yang dapat mendengarkan beacon lab");
        return joined > 0;
    }
}
