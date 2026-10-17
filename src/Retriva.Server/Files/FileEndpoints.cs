using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Retriva.Shared.Config;

namespace Retriva.Server.Files;

/// <summary>
/// Titik unduh berkas untuk agent.
///
/// Berjalan di Kestrel yang sama dengan hub SignalR, jadi ia mewarisi TLS dan
/// sertifikat yang sidik jarinya sudah disematkan di tiap agent. Tidak ada
/// port baru yang perlu dibuka di firewall, dan tidak ada kepercayaan baru
/// yang perlu dibangun.
/// </summary>
public static class FileEndpoints
{
    public const string Path = "/files";

    public static void MapRetrivaFiles(this WebApplication app)
    {
        app.MapGet(Path + "/{id}", (string id, HttpContext context) =>
        {
            var vault = context.RequestServices.GetRequiredService<FileVault>();
            var config = context.RequestServices.GetRequiredService<ConsoleConfig>();

            // Kode pendaftaran dipakai ulang sebagai bukti bahwa peminta memang
            // anggota lab ini. Sudah dimiliki setiap agent yang terdaftar, dan
            // tidak pernah menyeberangi jaringan dalam keadaan terbuka karena
            // seluruh sambungan ini ber-TLS.
            //
            var registry = context.RequestServices.GetService<UnitRegistry>();
            var remoteIp = context.Connection.RemoteIpAddress;

            var presentedSecret = context.Request.Headers["X-Retriva-Enrollment"].ToString();
            var presentedToken = context.Request.Headers["X-Retriva-Token"].ToString();
            var presentedUnit = context.Request.Headers["X-Retriva-Unit"].ToString();

            // Autentikasi:
            // 1. Jika kode pendaftaran di server tidak disetel, izinkan.
            // 2. Jika kode pendaftaran cocok, izinkan.
            // 3. Jika unit mengirim unit id / token yang sudah terdaftar di lab, izinkan.
            // 4. Jika IP peminta adalah salah satu unit yang aktif terhubung di lab atau localhost, izinkan.
            var authorized = false;
            if (string.IsNullOrEmpty(config.AgentEnrollmentSecret))
            {
                authorized = true;
            }
            else if (!string.IsNullOrEmpty(presentedSecret) && string.Equals(presentedSecret, config.AgentEnrollmentSecret, StringComparison.Ordinal))
            {
                authorized = true;
            }
            else if (registry != null)
            {
                var units = registry.Snapshot();
                if (!string.IsNullOrEmpty(presentedUnit) && units.Any(u => string.Equals(u.UnitId, presentedUnit, StringComparison.OrdinalIgnoreCase)))
                {
                    authorized = true;
                }
                else if (remoteIp != null)
                {
                    var ipStr = (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString();
                    if (ipStr == "127.0.0.1" || ipStr == "::1" || units.Any(u => u.IpAddress == ipStr))
                    {
                        authorized = true;
                    }
                }
            }

            if (!authorized)
            {
                return Results.Unauthorized();
            }

            var staged = vault.Find(id);
            if (staged is null) return Results.NotFound();

            var stream = vault.Open(id);
            if (stream is null) return Results.NotFound();

            // enableRangeProcessing membuat unduhan yang putus dapat dilanjutkan
            // dari posisi terakhir, bukan diulang dari nol. Untuk materi 500 MB
            // di jaringan lab yang sesekali tersendat, perbedaannya besar.
            return Results.File(stream, "application/octet-stream",
                fileDownloadName: staged.FileName, enableRangeProcessing: true);
        });
    }
}
