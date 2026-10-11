using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>
/// Memeriksa berkala unit mana yang berhenti berdenyut lalu menandainya offline.
/// </summary>
public sealed class UnitWatchdog(UnitRegistry registry, BatchTracker batches, ILogger<UnitWatchdog> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(RetrivaProtocol.HeartbeatTimeoutSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (registry.SweepStale(Timeout))
                log.LogInformation("Ada unit yang melewati batas heartbeat dan ditandai offline");

            // Unit yang mati mendadak tidak pernah membalas perintah massal.
            batches.SweepExpired();
        }
    }
}
