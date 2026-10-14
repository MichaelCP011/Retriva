using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Retriva.Shared.Update;

/// <summary>
/// Layanan pembaruan mandiri (Self-Update):
/// Memeriksa rilis baru di GitHub Releases, mengunduh installer, dan menjalankannya
/// secara senyap tanpa perlu uninstall manual.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Retriva", RetrivaVersion.Current));
    }

    /// <summary>
    /// Memeriksa apakah ada rilis terbaru di GitHub Releases yang versinya lebih baru
    /// dari versi aplikasi saat ini.
    /// </summary>
    public static async Task<UpdateInfo?> CheckGitHubReleaseAsync(
        string? owner = null,
        string? repo = null,
        CancellationToken cancellationToken = default)
    {
        owner ??= RetrivaVersion.GitHubOwner;
        repo ??= RetrivaVersion.GitHubRepo;

        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElem)) return null;
            var tagName = tagElem.GetString() ?? "";

            if (!RetrivaVersion.IsNewer(tagName)) return null;

            var title = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? tagName : tagName;
            var body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
            var publishedAt = root.TryGetProperty("published_at", out var pubElem) && pubElem.TryGetDateTimeOffset(out var dt)
                ? dt
                : DateTimeOffset.UtcNow;

            string? downloadUrl = null;
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                        assetSize = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        if (assetName.Equals("RetrivaSetup.exe", StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

            return new UpdateInfo
            {
                Version = tagName.TrimStart('v', 'V'),
                TagName = tagName,
                Title = title,
                ReleaseNotes = body,
                DownloadUrl = downloadUrl,
                AssetSizeBytes = assetSize,
                PublishedAt = publishedAt,
            };
        }
        catch
        {
            // Pengecekan pembaruan tidak boleh menyebabkan aplikasi crash jika offline
            return null;
        }
    }

    /// <summary>
    /// Mengunduh installer baru ke folder sementara dengan pelaporan kemajuan.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string? targetPath = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        targetPath ??= Path.Combine(Path.GetTempPath(), $"RetrivaSetup-{Guid.NewGuid():N}.exe");

        var tempFile = targetPath + ".tmp";

        using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes);
                }
            }
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(tempFile, targetPath);

        return targetPath;
    }

    /// <summary>
    /// Menjalankan installer pembaruan secara senyap (silent) di latar belakang.
    /// </summary>
    public static bool ApplySilentInstall(string installerPath, string role)
    {
        if (!File.Exists(installerPath)) return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = $"--silent --role {role}",
            UseShellExecute = true,
            CreateNoWindow = true,
            Verb = "runas" // Minta hak administrator bila perlu
        };

        var process = Process.Start(startInfo);
        return process != null;
    }
}
