using System.Net.Security;
using Microsoft.AspNetCore.SignalR.Client;
using Retriva.Shared.Config;
using Retriva.Shared.Security;

namespace Retriva.Agent;

/// <summary>
/// Membangun HubConnection ber-TLS dengan sertifikat konsol yang disematkan.
///
/// Perlu dua callback terpisah: negosiasi awal lewat HTTP, lalu transport
/// naik ke WebSocket yang punya jalur validasi sertifikat sendiri.
/// Melewatkan salah satunya membuat penyematan bocor tanpa terlihat.
/// </summary>
public static class ConsoleConnection
{
    public static HubConnection Build(string endpoint, AgentConfig config, string hubPath)
    {
        var url = $"https://{endpoint}:{config.ConsolePort}{hubPath}";
        var pin = config.ConsoleCertificateThumbprint;

        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http)
                    {
                        http.ServerCertificateCustomValidationCallback =
                            (_, certificate, _, _) => CertificatePin.Matches(certificate, pin);
                    }
                    return handler;
                };

                options.WebSocketConfiguration = socket =>
                {
                    socket.RemoteCertificateValidationCallback =
                        (_, certificate, _, _) => CertificatePin.Matches(certificate, pin);
                };
            })
            .WithAutomaticReconnect(new JitteredRetryPolicy())
            .Build();
    }

    /// <summary>
    /// Kemudahan saat pengembangan di satu mesin: bila konfigurasi belum
    /// berisi sidik jari, ambil dari berkas yang ditulis konsol lokal.
    /// Di lab sungguhan nilai ini ditanamkan installer, bukan diambil begini.
    /// </summary>
    public static bool TryAdoptLocalTrust(AgentConfig config, ILogger log)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(config.ConsoleCertificateThumbprint)
            && File.Exists(Retriva.Shared.RetrivaPaths.ThumbprintFile))
        {
            config.ConsoleCertificateThumbprint =
                File.ReadAllText(Retriva.Shared.RetrivaPaths.ThumbprintFile).Trim();
            log.LogWarning("Sidik jari sertifikat diambil dari konsol lokal — hanya untuk pengembangan");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(config.EnrollmentSecret)
            && File.Exists(Retriva.Shared.RetrivaPaths.ConsoleConfigFile))
        {
            var console = ConfigStore.LoadOrCreate<ConsoleConfig>(
                Retriva.Shared.RetrivaPaths.ConsoleConfigFile,
                problem => log.LogWarning("{Problem}", problem));
            if (!string.IsNullOrWhiteSpace(console.AgentEnrollmentSecret))
            {
                config.EnrollmentSecret = console.AgentEnrollmentSecret;
                log.LogWarning("Kode pendaftaran diambil dari konsol lokal — hanya untuk pengembangan");
                changed = true;
            }
        }

        return changed;
    }
}
