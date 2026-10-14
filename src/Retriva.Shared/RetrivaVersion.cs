namespace Retriva.Shared;

/// <summary>
/// Informasi versi terpusat aplikasi Retriva.
/// Semua komponen (Admin, Agent, Client, Setup) merujuk ke sini supaya tidak ada
/// versi yang tertinggal saat rilis baru diterbitkan.
/// </summary>
public static class RetrivaVersion
{
    /// <summary>Nomor versi saat ini (SemVer: Major.Minor.Patch).</summary>
    public const string Current = "1.0.0";

    /// <summary>Pemilik dan repositori GitHub resmi untuk rilis dan pembaruan.</summary>
    public const string GitHubOwner = "MichaelCP011";
    public const string GitHubRepo = "Retriva";

    /// <summary>Membandingkan apakah versi kandidat lebih baru daripada versi saat ini.</summary>
    public static bool IsNewer(string candidate)
    {
        var cleanCandidate = Clean(candidate);
        var cleanCurrent = Clean(Current);

        if (Version.TryParse(cleanCandidate, out var vCandidate) &&
            Version.TryParse(cleanCurrent, out var vCurrent))
        {
            return vCandidate > vCurrent;
        }

        return string.Compare(cleanCandidate, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string Clean(string v) => v.Trim().TrimStart('v', 'V');
}
