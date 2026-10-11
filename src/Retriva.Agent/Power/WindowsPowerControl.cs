using System.Diagnostics;
using System.Runtime.Versioning;

namespace Retriva.Agent.Power;

/// <summary>
/// Memakai shutdown.exe alih-alih InitiateSystemShutdownEx.
///
/// Alasannya pragmatis: jalur API menuntut pengaktifan hak SE_SHUTDOWN_NAME
/// lewat OpenProcessToken/AdjustTokenPrivileges — sekitar seratus baris
/// P/Invoke yang gagalnya sunyi dan sulit dilacak. shutdown.exe sudah
/// menangani semua itu, ada di setiap Windows, dan bisa diuji manual dari
/// terminal dengan perintah yang sama persis.
///
/// Bila kelak dibutuhkan kontrol lebih halus (mis. alasan shutdown terekam
/// di Event Log dengan kode khusus), InitiateSystemShutdownEx adalah jalurnya.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerControl(ILogger log) : IPowerControl
{
    public PowerResult Shutdown(int delaySeconds, bool force, string message)
        => Run($"/s /t {Math.Max(0, delaySeconds)} /c \"{Sanitize(message)}\"" + (force ? " /f" : ""),
               $"shutdown dalam {delaySeconds} detik");

    public PowerResult Restart(int delaySeconds, string message)
        => Run($"/r /t {Math.Max(0, delaySeconds)} /c \"{Sanitize(message)}\"",
               $"restart dalam {delaySeconds} detik");

    public PowerResult Abort()
        => Run("/a", "hitung mundur dibatalkan");

    private PowerResult Run(string arguments, string description)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            });

            if (process is null) return new PowerResult(false, "shutdown.exe tidak dapat dijalankan");

            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                log.LogWarning("shutdown.exe {Args} gagal ({Code}): {Error}",
                    arguments, process.ExitCode, error);

                // 1116 = tidak ada shutdown yang sedang berjalan (saat /a dipanggil)
                return new PowerResult(false,
                    process.ExitCode == 1116 ? "tidak ada hitung mundur yang berjalan" : error);
            }

            log.LogInformation("Perintah daya dijalankan: {Description}", description);
            return new PowerResult(true, description);
        }
        catch (Exception ex)
        {
            log.LogError("Perintah daya gagal: {Message}", ex.Message);
            return new PowerResult(false, ex.Message);
        }
    }

    /// <summary>Menutup celah penyuntikan argumen lewat pesan yang datang dari jaringan.</summary>
    private static string Sanitize(string message)
    {
        var clean = new string(message.Where(c => c != '"' && c != '\r' && c != '\n').ToArray());
        return clean.Length > 200 ? clean[..200] : clean;
    }
}
