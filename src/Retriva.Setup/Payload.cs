using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Retriva.Setup;

/// <summary>
/// Muatan yang ditanam di dalam RetrivaSetup.exe.
///
/// SATU BERKAS, BUKAN FOLDER BERISI PULUHAN BERKAS.
///
/// Installer yang berupa folder akan disalin setengah oleh siapa pun yang
/// memindahkannya lewat flash disk, dan setengahnya tidak akan terlihat kurang
/// sampai pemasangan berjalan. Satu .exe tidak dapat dipindahkan separuh.
///
/// ZIP-nya dibuat oleh build-installer.ps1 lalu ditanam sebagai EmbeddedResource,
/// sehingga urutannya: publish → zip → build installer. Dua tahap, karena
/// muatannya adalah hasil build proyek lain.
/// </summary>
public static class Payload
{
    /// <summary>Nama resource untuk tiap peran pemasangan.</summary>
    public static string ResourceFor(SetupRole role) => role switch
    {
        SetupRole.Lab => "Retriva.Setup.payload.lab.zip",
        SetupRole.Panel => "Retriva.Setup.payload.panel.zip",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>
    /// True bila muatan untuk peran ini benar-benar ada di dalam exe.
    ///
    /// Diperiksa SEBELUM wizard menawarkan perannya. Installer yang menampilkan
    /// pilihan "PC Mahasiswa" lalu gagal di langkah ekstraksi karena muatannya
    /// tidak ikut terbungkus adalah kegagalan yang menyalahkan mesin orangnya,
    /// padahal sebabnya ada di build.
    /// </summary>
    public static bool Has(SetupRole role) =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Contains(ResourceFor(role), StringComparer.Ordinal);

    /// <summary>Ukuran muatan terkompresi, untuk ditampilkan sebelum memasang.</summary>
    public static long CompressedSize(SetupRole role)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceFor(role));
        return stream?.Length ?? 0;
    }

    /// <summary>
    /// Mengeluarkan muatan ke folder tujuan.
    ///
    /// Setiap berkas diperiksa BENAR-BENAR tertulis setelah ekstraksi. ZipArchive
    /// tidak melempar pengecualian untuk entri yang gagal ditulis karena disk
    /// penuh di tengah jalan; ia hanya menulis lebih sedikit byte. Pemasangan
    /// lalu berlanjut, layanan didaftarkan, dan yang gagal adalah agent yang
    /// tidak mau start dengan pesan tentang assembly yang hilang.
    /// </summary>
    public static void Extract(SetupRole role, string targetDir, Action<string>? log = null)
    {
        var name = ResourceFor(role);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Muatan {name} tidak ada di dalam installer ini. "
              + "Installer dibangun tanpa menjalankan tahap pembungkusan muatan.");

        Directory.CreateDirectory(targetDir);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var expected = 0;

        foreach (var entry in archive.Entries)
        {
            // Entri berakhiran '/' adalah folder, tanpa isi.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

            /*
                Jalur diperiksa tetap berada DI DALAM folder tujuan.

                Entri ZIP boleh berisi "..\..\Windows\System32\sesuatu.dll", dan
                ekstraksi yang menurut saja akan menulis ke sana dengan hak
                administrator. Muatan ini dibuat oleh build kita sendiri, jadi
                ancamannya kecil — tetapi pemeriksaannya dua baris, dan installer
                yang berjalan sebagai administrator bukan tempat untuk berhemat
                dua baris.
            */
            if (!destination.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Entri muatan keluar dari folder tujuan: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            var written = new FileInfo(destination);
            if (!written.Exists || written.Length != entry.Length)
            {
                throw new InvalidOperationException(
                    $"{entry.FullName} tidak tertulis utuh "
                  + $"({written.Length:N0} dari {entry.Length:N0} byte) — periksa ruang disk");
            }

            expected++;
        }

        log?.Invoke($"{expected} berkas diekstrak ke {targetDir}");
    }
}
