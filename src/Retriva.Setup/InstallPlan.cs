namespace Retriva.Setup;

/// <summary>Satu langkah pemasangan, beserta cara membatalkannya.</summary>
/// <param name="Title">Kalimat yang muncul di layar kemajuan.</param>
/// <param name="Apply">Tindakannya.</param>
/// <param name="Undo">
/// Pembatalannya. Boleh null hanya untuk langkah yang memang tidak mengubah
/// apa pun di mesin — misalnya pemeriksaan. Setiap langkah yang menyentuh
/// disk, layanan, atau registry WAJIB punya pembatalan.
/// </param>
public sealed record InstallStep(string Title, Action Apply, Action? Undo);

/// <summary>
/// Menjalankan langkah pemasangan, dan membatalkannya bila ada yang gagal.
///
/// PEMBATALAN ADALAH ALASAN BERKAS INI ADA.
///
/// Pemasangan yang berhasil tidak menuntut apa pun yang istimewa: salin berkas,
/// daftarkan layanan, tulis registry. Yang menuntut perhatian adalah pemasangan
/// yang gagal di langkah kelima dari delapan. Tanpa pembatalan, mesin itu
/// tertinggal dengan layanan terdaftar yang menunjuk ke folder separuh terisi —
/// keadaan yang tidak dapat diperbaiki dengan memasang ulang (layanan sudah
/// ada), tidak dapat dihapus lewat Add/Remove Programs (entrinya belum sempat
/// ditulis), dan tidak menghasilkan pesan yang menjelaskan apa pun.
///
/// Di lab empat puluh PC, satu mesin dalam keadaan itu akan tinggal begitu
/// sampai ada yang menyerah dan memasang ulang Windows.
///
/// Langkah yang sudah selesai dicatat, dan kegagalan membatalkannya MUNDUR —
/// urutan terbalik, karena langkah belakangan bergantung pada yang sebelumnya.
/// Menghapus folder sebelum melepas layanan yang memakainya akan gagal.
/// </summary>
public sealed class InstallPlan(Action<string> log)
{
    private readonly List<InstallStep> _steps = [];

    public IReadOnlyList<InstallStep> Steps => _steps;

    public InstallPlan Add(string title, Action apply, Action? undo)
    {
        _steps.Add(new InstallStep(title, apply, undo));
        return this;
    }

    /// <summary>
    /// Menjalankan seluruh langkah. Mengembalikan null bila berhasil, atau
    /// alasan kegagalan setelah pembatalan selesai.
    /// </summary>
    /// <param name="onProgress">Nomor langkah (mulai 1), total, judul.</param>
    public string? Run(Action<int, int, string>? onProgress = null)
    {
        var done = new Stack<InstallStep>();

        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            onProgress?.Invoke(i + 1, _steps.Count, step.Title);
            log($"[{i + 1}/{_steps.Count}] {step.Title}");

            try
            {
                step.Apply();

                // Dicatat SETELAH berhasil. Langkah yang gagal di tengah sudah
                // membersihkan dirinya sendiri lewat pengecualian, dan
                // membatalkannya lagi dapat menghapus hal yang bukan miliknya.
                done.Push(step);
            }
            catch (Exception ex)
            {
                log($"GAGAL: {ex.Message}");
                Rollback(done);
                return ex.Message;
            }
        }

        return null;
    }

    private void Rollback(Stack<InstallStep> done)
    {
        if (done.Count == 0)
        {
            log("Tidak ada yang perlu dibatalkan — kegagalan terjadi di langkah pertama.");
            return;
        }

        log($"Membatalkan {done.Count} langkah yang sudah selesai...");

        while (done.Count > 0)
        {
            var step = done.Pop();
            if (step.Undo is null) continue;

            /*
                Pembatalan yang gagal DICATAT, bukan menghentikan pembatalan.

                Kalau satu pembatalan melempar pengecualian dan seluruh proses
                berhenti di situ, sisa langkah di bawahnya tidak pernah
                dibatalkan — dan mesin tertinggal lebih berantakan daripada bila
                pembatalannya tidak pernah dicoba sama sekali.

                Yang gagal dibatalkan tetap perlu diketahui, jadi ia masuk log
                dengan namanya. Orang yang membacanya tahu persis apa yang
                tersisa di mesin itu.
            */
            try
            {
                step.Undo();
                log($"  dibatalkan: {step.Title}");
            }
            catch (Exception ex)
            {
                log($"  GAGAL dibatalkan: {step.Title} — {ex.Message}");
            }
        }

        log("Pembatalan selesai. Mesin dikembalikan ke keadaan sebelum pemasangan.");
    }
}
