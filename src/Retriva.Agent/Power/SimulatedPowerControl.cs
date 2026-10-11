namespace Retriva.Agent.Power;

/// <summary>
/// Mencatat perintah tanpa menjalankannya. Dipakai di mesin pengembangan
/// supaya menekan tombol di dashboard tidak mematikan laptop sendiri.
/// </summary>
public sealed class SimulatedPowerControl(ILogger log) : IPowerControl
{
    public PowerResult Shutdown(int delaySeconds, bool force, string message)
        => Log($"SHUTDOWN dalam {delaySeconds} detik (force={force}) — \"{message}\"");

    public PowerResult Restart(int delaySeconds, string message)
        => Log($"RESTART dalam {delaySeconds} detik — \"{message}\"");

    public PowerResult Abort()
        => Log("BATALKAN hitung mundur");

    private PowerResult Log(string what)
    {
        log.LogWarning("[simulasi daya] {What} — tidak dieksekusi", what);
        return new PowerResult(true, "disimulasikan: " + what);
    }
}
