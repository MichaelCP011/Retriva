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
            // Ini bukan pengganti autentikasi per-unit yang sesungguhnya, dan
            // memang tidak berpura-pura menjadi itu — M7.3 yang menggantinya
            // dengan kredensial per perangkat.
            var presented = context.Request.Headers["X-Retriva-Enrollment"].ToString();
            if (!string.IsNullOrEmpty(config.AgentEnrollmentSecret)
                && !string.Equals(presented, config.AgentEnrollmentSecret, StringComparison.Ordinal))
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
