using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Memancarkan keberadaan lab ini ke subnet.
///
/// MENGGANTIKAN PENGETIKAN ALAMAT DAN KODE.
///
/// Versi sebelumnya menuntut dosen mengetik alamat IP dan kode dua belas
/// karakter. Itu membebankan pekerjaan pada orang yang salah: kode dan alamat
/// adalah urusan mesin, dan dosen hanya ingin masuk ke labnya.
///
/// Beacon ini membuat panel dosen menemukan labnya sendiri. Yang tersisa untuk
/// manusia hanyalah satu tombol di sisi dosen dan satu persetujuan di sisi
/// asisten — dan itu memang dua keputusan yang benar-benar perlu diambil orang.
///
/// TIDAK MEMBERI WEWENANG APA PUN. Menemukan lab hanya berarti tahu ke mana
/// menyambung. Panel yang datang tetap masuk sebagai Pending, dan yang
/// menentukan perannya tetap orang di PC Asisten.
/// </summary>
public sealed class LabBeaconService(
    ConsoleConfig config,
    PanelEnrollment enrollment,
    CoordinatorIdentity identity,
    ILogger<LabBeaconService> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Parse(PanelProtocol.DiscoveryGroup),
                                    PanelProtocol.DiscoveryPort);

        /*
            TTL dinaikkan menjadi 2, bukan dibiarkan pada nilai bawaan 1.

            TTL 1 membuat datagram berhenti di router pertama. Di lab dengan
            satu switch itu cukup, tetapi begitu ada router antara meja dosen
            dan PC Asisten — misalnya jaringan Wi-Fi tamu yang terpisah — labnya
            berhenti muncul tanpa satu pun tanda kegagalan. Dua melewati satu
            batas router, dan tetap tidak akan keluar dari gedung.
        */
        socket.Ttl = 2;

        log.LogInformation("Beacon lab dipancarkan ke {Group}:{Port} tiap {Seconds:F0} detik",
            PanelProtocol.DiscoveryGroup, PanelProtocol.DiscoveryPort,
            PanelProtocol.BeaconInterval.TotalSeconds);

        using var timer = new PeriodicTimer(PanelProtocol.BeaconInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var beacon = new LabBeacon
            {
                LabName = config.LabName,

                // Alamat dikosongkan; panel memakai alamat pengirim datagram.
                //
                // Alamat yang diisi sendiri oleh Superadmin akan salah persis
                // pada mesin yang paling sering salah: yang punya beberapa
                // kartu jaringan, VPN, atau adaptor virtual. Pengirim datagram
                // selalu benar menurut definisi — itulah jalur yang benar-benar
                // sampai.
                Host = "",
                Port = config.ListenPort,
                AcceptingPanels = enrollment.IsOpen,
                Thumbprint = identity.Thumbprint,
            };

            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beacon, Json));

            try
            {
                await socket.SendAsync(payload, target, stoppingToken);
            }
            catch (SocketException ex)
            {
                // Multicast diblokir sebagian jaringan. Bukan alasan berhenti:
                // penautan manual lewat alamat tetap tersedia, dan mencatatnya
                // sekali per kegagalan membuat sebabnya terlihat bila nanti
                // ada yang bertanya mengapa labnya tidak pernah muncul.
                log.LogWarning("Beacon gagal dipancarkan: {Message}", ex.Message);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
