namespace Retriva.Shared.Update;

/// <summary>
/// Informasi rilis pembaruan baru yang ditemukan dari GitHub Releases atau server lokal.
/// </summary>
public sealed record UpdateInfo
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string Title { get; init; }
    public required string ReleaseNotes { get; init; }
    public required string DownloadUrl { get; init; }
    public required long AssetSizeBytes { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}
