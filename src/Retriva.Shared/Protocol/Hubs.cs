namespace Retriva.Shared.Protocol;

/// <summary>Konstanta bersama supaya kedua sisi tidak salah ketik.</summary>
public static class RetrivaProtocol
{
    public const string Version = "1.0";
    public const string HubPath = "/hub/agent";
    public const int DefaultConsolePort = 8443;
    public const int DefaultBroadcastPort = 9000;

    /// <summary>Selang heartbeat yang diminta konsol kepada agent.</summary>
    public const int HeartbeatSeconds = 5;

    /// <summary>
    /// Batas diam sebelum unit dianggap mati. Sengaja tiga kali selang heartbeat:
    /// satu paket hilang tidak boleh membuat unit langsung dinyatakan offline,
    /// tetapi PC yang kabelnya dicabut harus ketahuan dalam belasan detik.
    /// </summary>
    public const int HeartbeatTimeoutSeconds = HeartbeatSeconds * 3;
}

/// <summary>
/// Metode yang dipanggil agent PADA konsol.
/// Diimplementasikan konsol sebagai SignalR Hub.
/// </summary>
public interface IConsoleHub
{
    Task<RegisterResponse> Register(RegisterRequest request);
    Task Heartbeat(HeartbeatReport report);
    Task PublishThumbnail(ThumbnailFrame frame);
    Task ReportCommandResult(CommandResult result);
    Task SendChat(ChatMessage message);
    Task ReportExamEvent(ExamEvent evt);
}

/// <summary>
/// Metode yang dipanggil konsol PADA agent.
/// Diimplementasikan agent sebagai handler SignalR client.
/// </summary>
public interface IAgentClient
{
    Task Execute(AgentCommand command);
    Task ReceiveChat(ChatMessage message);
    /// <summary>Konsol meminta agent memutus dan menyambung ulang, mis. setelah perubahan konfigurasi.</summary>
    Task Reconnect(string? newEndpoint);
}
