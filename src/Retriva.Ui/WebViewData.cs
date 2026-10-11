using System.IO;

namespace Retriva.Ui;

/// <summary>
/// Folder data WebView2, ditentukan secara eksplisit.
///
/// TANPA INI, APLIKASI TIDAK DAPAT DIJALANKAN DARI PROGRAM FILES.
///
/// WebView2 menyimpan cache, cookie, dan berkas kerjanya di sebuah folder yang
/// harus dapat DITULIS. Bila tidak diberi tahu ke mana, ia membuatnya di
/// sebelah exe: "Retriva.exe.WebView2\".
///
/// Selama pengembangan itu tidak pernah terlihat salah — exe berjalan dari
/// bin\Debug di drive kerja, yang tentu saja dapat ditulis. Begitu aplikasi
/// dipasang ke C:\Program Files, folder itu hanya dapat ditulis administrator,
/// dan pembuatan lingkungan WebView2 gagal dengan:
///
///     Access is denied. (0x80070005 (E_ACCESSDENIED))
///
/// HRESULT dari COM, tanpa menyebut WebView2, tanpa menyebut Program Files, dan
/// tanpa menyebut folder mana yang ditolak. Aplikasinya sekadar berhenti.
///
/// Inilah kelas kegagalan yang tidak akan pernah muncul selama pengujian di
/// mesin pengembang, dan muncul pada setiap mesin yang dipasang dengan benar.
///
/// LocalAppData, bukan ProgramData: isinya milik satu pengguna — cache dan
/// cookie halaman yang ia buka — dan setiap pengguna Windows selalu dapat
/// menulis ke folder miliknya sendiri tanpa hak khusus. ProgramData akan
/// menuntut pengaturan izin, dan menyatukan cache beberapa pengguna di satu
/// tempat adalah masalah yang tidak perlu dibuat.
/// </summary>
public static class WebViewData
{
    /// <summary>
    /// Folder data untuk sebuah aplikasi Retriva.
    /// </summary>
    /// <param name="appName">"admin" atau "client" — dipisah supaya panel dan
    /// klien yang berjalan bersamaan di satu PC tidak berebut folder yang sama.
    /// WebView2 mengunci foldernya, dan proses kedua akan gagal.</param>
    public static string FolderFor(string appName)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Retriva", "WebView2", appName);

        Directory.CreateDirectory(path);
        return path;
    }
}
