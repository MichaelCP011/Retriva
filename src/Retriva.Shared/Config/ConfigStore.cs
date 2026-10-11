using System.Text.Json;

namespace Retriva.Shared.Config;

/// <summary>
/// Pemuat konfigurasi JSON. Sengaja JSON, bukan basis data — isinya sedikit,
/// jarang berubah, satu penulis, dan enak dibaca serta diedit manusia.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Memuat konfigurasi. Bila berkas belum ada, dibuat dengan nilai bawaan.
    ///
    /// Bila berkas ADA tetapi gagal di-parse, berkasnya TIDAK disentuh sama
    /// sekali dan nilai bawaan hanya dipakai di memori. Menimpa konfigurasi
    /// yang susah payah diedit pengguna hanya karena ada satu koma salah
    /// adalah perilaku yang jauh lebih merugikan daripada berhenti dan mengeluh.
    /// </summary>
    public static T LoadOrCreate<T>(string path, Action<string>? onProblem = null) where T : new()
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<T>(json, Options);
                if (loaded is not null) return loaded;

                onProblem?.Invoke($"Konfigurasi {path} kosong atau bernilai null. Memakai nilai bawaan di memori.");
            }
            catch (JsonException ex)
            {
                onProblem?.Invoke(
                    $"Konfigurasi {path} tidak dapat dibaca: {ex.Message}. "
                  + "Berkas dibiarkan apa adanya; nilai bawaan dipakai untuk sesi ini saja.");
            }
            catch (IOException ex)
            {
                onProblem?.Invoke($"Konfigurasi {path} tidak dapat diakses: {ex.Message}");
            }

            // Penting: tidak menyimpan apa pun. Berkas pengguna tetap utuh.
            return new T();
        }

        var fresh = new T();
        Save(path, fresh);
        return fresh;
    }

    public static void Save<T>(string path, T value)
    {
        RetrivaPaths.EnsureDataDir();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Tulis ke berkas sementara lalu ganti, supaya listrik mati di tengah
        // penulisan tidak meninggalkan konfigurasi yang terpotong.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
