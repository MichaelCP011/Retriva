using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Retriva.Shared;
using Retriva.Shared.Security;

namespace Retriva.Server.Security;

/// <summary>
/// Membuat dan memuat sertifikat self-signed milik konsol. Tidak ada CA:
/// agent mempercayai sertifikat ini semata-mata karena sidik jarinya cocok.
/// </summary>
public static class CertificateManager
{
    // Kata sandi PFX hanya melindungi berkas saat transit di disk lokal;
    // keamanan sesungguhnya bertumpu pada penyematan sidik jari di agent.
    private const string PfxPassword = "retriva-local";

    public static X509Certificate2 LoadOrCreate()
    {
        RetrivaPaths.EnsureDataDir();

        if (File.Exists(RetrivaPaths.CertificateFile))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(RetrivaPaths.CertificateFile),
                    PfxPassword,
                    X509KeyStorageFlags.Exportable);

                if (existing.NotAfter > DateTime.Now.AddDays(30))
                {
                    WriteThumbprint(existing);
                    return existing;
                }
            }
            catch (CryptographicException)
            {
                // Berkas rusak — buat ulang di bawah.
            }
        }

        /*
            SERTIFIKAT BARU HARUS DIPUTAR LEWAT PFX SEBELUM DIPAKAI.

            Ini bug yang menghabiskan waktu paling lama untuk ditemukan, dan
            bentuknya khas: jalannya benar sembilan dari sepuluh kali.

            CreateSelfSigned menghasilkan sertifikat yang kunci privatnya hidup
            di memori proses saja — tidak terdaftar di penyimpanan kunci
            Windows. SChannel, yang dipakai Kestrel untuk TLS di Windows, tidak
            dapat memakai kunci semacam itu. Ia menerima ClientHello, gagal
            diam-diam, lalu menutup soket tanpa menjawab apa pun.

            Di sisi klien gejalanya:
                "The SSL connection could not be established"
                <- "Received an unexpected EOF or 0 bytes from the transport stream"

            Tidak ada yang menyebut sertifikat, tidak ada yang menyebut kunci,
            dan tidak ada satu baris pun galat di sisi server.

            YANG MEMBUATNYA SULIT TERLIHAT: hanya jalankan PERTAMA yang rusak.
            Jalankan berikutnya memuat berkas PFX lewat LoadPkcs12, yang
            memasukkan kuncinya ke penyimpanan Windows dan menghasilkan
            sertifikat yang berfungsi. Jadi bug ini tidur sepanjang bulan-bulan
            pengembangan, lalu bangun tepat setelah folder data dibersihkan.

            Memuat ulang dari byte yang baru ditulis menyamakan kedua jalur:
            yang dipakai selalu sertifikat hasil impor, tidak pernah yang
            langsung dari pembuatan.
        */
        using var created = Create();
        var pfx = created.Export(X509ContentType.Pfx, PfxPassword);
        File.WriteAllBytes(RetrivaPaths.CertificateFile, pfx);

        var usable = X509CertificateLoader.LoadPkcs12(
            pfx, PfxPassword, X509KeyStorageFlags.Exportable);

        WriteThumbprint(usable);
        return usable;
    }

    private static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Retriva Console", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LocalAddresses()) san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
    }

    /// <summary>
    /// Menulis sidik jari ke berkas teks. Installer menyalin nilai ini ke
    /// konfigurasi agent; saat pengembangan di satu mesin, agent membacanya langsung.
    /// </summary>
    private static void WriteThumbprint(X509Certificate2 certificate)
    {
        try
        {
            File.WriteAllText(RetrivaPaths.ThumbprintFile, CertificatePin.Thumbprint(certificate));
        }
        catch (IOException) { /* tidak fatal */ }
    }

    private static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);
}
