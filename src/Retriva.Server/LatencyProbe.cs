using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Mengukur waktu pulang-pergi ke tiap unit, berkala.
///
/// PULANG-PERGI, BUKAN SATU ARAH — DAN ITU BUKAN KEMEWAHAN.
///
/// Godaan yang lebih murah adalah mengurangi SentAt di denyut dari waktu tiba
/// di koordinator. Angkanya keluar tanpa lalu lintas tambahan, dan angkanya
/// salah: kedua mesin punya jam sendiri-sendiri, dan PC lab yang meleset satu
/// menit dari koordinator akan melaporkan latensi enam puluh ribu milidetik
/// atau angka negatif. Keduanya tampak seperti hasil pengukuran.
///
/// Di sini kedua ujung waktunya dibaca dari SATU jam — jam koordinator — jadi
/// selisih jam antar mesin tidak dapat menyusup ke dalam hasilnya. Yang diukur
/// mencakup antrean SignalR dan penjadwalan di unit, bukan hanya kabelnya, dan
/// itu memang yang ingin diketahui: seberapa cepat unit itu menjawab perintah,
/// bukan seberapa cepat elektronnya bergerak.
///
/// Diukur satu per satu, bukan serentak. Empat puluh permintaan yang
/// dilepaskan bersamaan akan saling antre di koordinator, dan yang terukur
/// justru antreannya sendiri.
/// </summary>
public sealed class LatencyProbe(
    UnitRegistry registry,
    IHubContext<AgentHub> agents,
    ILogger<LatencyProbe> log) : BackgroundService
{
    /// <summary>
    /// Cukup jarang untuk tidak menambah beban yang berarti, cukup sering untuk
    /// menangkap jaringan yang memburuk sebelum praktikum dimulai.
    /// </summary>
    private static readonly TimeSpan Round = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Unit yang tidak menjawab dalam dua detik dianggap tidak terukur, bukan
    /// dianggap lambat dua ribu milidetik. Batas waktu yang dicatat sebagai
    /// hasil akan tampak sebagai jaringan buruk padahal unitnya sedang sibuk
    /// atau baru saja pergi.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Round);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var unit in registry.Snapshot())
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (unit.Status == UnitStatus.Offline) continue;
                if (registry.ConnectionIdFor(unit.UnitId) is not { } connection) continue;

                await ProbeAsync(unit.UnitId, connection, stoppingToken);
            }
        }
    }

    private async Task ProbeAsync(string unitId, string connection, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Patience);

        var watch = Stopwatch.StartNew();

        try
        {
            await agents.Clients.Client(connection).InvokeAsync<bool>("Ping", deadline.Token);
            registry.RecordLatency(unitId, watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Tidak terukur, dan dicatat sebagai tidak terukur. Membiarkan nilai
            // lama bertahan berarti peta menampilkan latensi sehat untuk unit
            // yang sudah berhenti menjawab sama sekali.
            registry.ClearLatency(unitId);
        }
        catch (Exception ex)
        {
            registry.ClearLatency(unitId);
            log.LogDebug("Ukur latensi {Unit} gagal: {Message}", unitId, ex.Message);
        }
    }
}
