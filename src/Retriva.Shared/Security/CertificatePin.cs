using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Retriva.Shared.Security;

/// <summary>
/// Penyematan sertifikat. Agent tidak mempercayai CA mana pun — ia hanya
/// menerima sertifikat yang sidik jarinya persis sama dengan yang disematkan.
///
/// Arah ini yang paling penting diamankan: penyusup yang menyamar sebagai
/// agent paling banter memalsukan satu unit, tetapi penyusup yang menyamar
/// sebagai konsol bisa mengunci 40 layar dan mematikan seluruh lab.
/// </summary>
public static class CertificatePin
{
    public static string Thumbprint(X509Certificate certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static bool Matches(X509Certificate? certificate, string? expected)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(expected))
        {
            Mismatch?.Invoke(expected ?? "(kosong)", certificate is null
                ? "(tidak ada sertifikat)"
                : Thumbprint(certificate));
            return false;
        }

        var normalized = expected.Replace(":", "").Replace(" ", "").Trim();
        var actual = Thumbprint(certificate);

        if (string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase)) return true;

        Mismatch?.Invoke(normalized, actual);
        return false;
    }

    /// <summary>
    /// Dipicu setiap penolakan, membawa sidik jari yang diharapkan dan yang
    /// benar-benar diterima.
    ///
    /// ADA KARENA TANPA INI PENOLAKANNYA TIDAK DAPAT DIBEDAKAN DARI APA PUN.
    ///
    /// Callback validasi TLS hanya boleh menjawab ya atau tidak. Sebuah "tidak"
    /// keluar dari .NET sebagai "The SSL connection could not be established,
    /// see inner exception" — kalimat yang sama persis untuk sidik jari yang
    /// tidak cocok, sertifikat kedaluwarsa, dan protokol yang tidak disepakati.
    ///
    /// Ketiganya menuntut perbaikan yang berbeda, dan menebaknya memakan waktu
    /// berjam-jam. Dua sidik jari yang dicetak berdampingan menjawabnya dalam
    /// satu pandangan.
    ///
    /// Static karena callback validasi tidak menyediakan tempat menitipkan
    /// apa pun, dan menyalurkan logger sampai ke dalamnya menuntut mengubah
    /// setiap tempat yang membangun sambungan.
    /// </summary>
    public static event Action<string, string>? Mismatch;
}
