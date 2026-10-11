namespace Retriva.Agent.Power;

public sealed record PowerResult(bool Success, string Detail);

/// <summary>
/// Kontrol daya di balik antarmuka supaya pengembangan bisa berjalan tanpa
/// benar-benar mematikan mesin, dan supaya implementasinya bisa ditukar
/// tanpa menyentuh kode pemanggil.
/// </summary>
public interface IPowerControl
{
    PowerResult Shutdown(int delaySeconds, bool force, string message);
    PowerResult Restart(int delaySeconds, string message);
    PowerResult Abort();
}
