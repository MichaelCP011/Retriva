using Microsoft.AspNetCore.SignalR.Client;

namespace Retriva.FakeAgent;

/// <summary>
/// Menyalin perilaku agent sungguhan. Justru di sinilah jitter paling terasa:
/// 40 unit dalam satu proses akan menyerbu konsol pada milidetik yang sama
/// bila jedanya seragam.
/// </summary>
internal sealed class JitteredRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var exponent = Math.Min(retryContext.PreviousRetryCount, 5);
        var baseDelay = Math.Min(30, Math.Pow(2, exponent));
        var jitter = Random.Shared.NextDouble() * baseDelay * 0.5;
        return TimeSpan.FromSeconds(baseDelay + jitter);
    }
}
