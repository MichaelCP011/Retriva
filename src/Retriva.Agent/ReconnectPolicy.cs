using Microsoft.AspNetCore.SignalR.Client;

namespace Retriva.Agent;

/// <summary>
/// Kebijakan coba-ulang dengan backoff eksponensial dan jitter acak.
///
/// Jitter-nya bukan hiasan. Dengan 40 agent di satu lab, konsol yang restart
/// akan disambut 40 permintaan koneksi pada detik yang persis sama, lalu
/// gelombang itu berulang tiap kali gagal. Menyebar percobaan secara acak
/// mencegah lonjakan serempak tersebut.
/// </summary>
public sealed class JitteredRetryPolicy : IRetryPolicy
{
    private const int MaxBackoffSeconds = 30;

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var exponent = Math.Min(retryContext.PreviousRetryCount, 5);
        var baseDelay = Math.Min(MaxBackoffSeconds, Math.Pow(2, exponent));

        // Jitter sampai 50% dari jeda dasar.
        var jitter = Random.Shared.NextDouble() * baseDelay * 0.5;

        // Tidak pernah mengembalikan null: agent harus terus mencoba selamanya,
        // karena konsol mungkin baru dinyalakan jauh setelah PC mahasiswa.
        return TimeSpan.FromSeconds(baseDelay + jitter);
    }

    /// <summary>Jeda sebelum mencoba endpoint berikutnya, juga diberi jitter.</summary>
    public static TimeSpan EndpointRetryDelay(int attempt)
    {
        var baseDelay = Math.Min(MaxBackoffSeconds, Math.Pow(2, Math.Min(attempt, 5)));
        return TimeSpan.FromSeconds(baseDelay + Random.Shared.NextDouble() * baseDelay * 0.5);
    }
}
