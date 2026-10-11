using Microsoft.Data.Sqlite;

namespace Retriva.Server.Data;

public sealed record ActivityEntry(
    string? UnitId, string Actor, string Action, string? Detail, DateTimeOffset CreatedAt);

/// <summary>
/// Jejak audit tindakan admin. Inilah salah satu alasan utama memakai basis
/// data: tabel ini tumbuh terus sepanjang semester, dan menulisnya ke JSON
/// berarti membaca lalu menulis ulang seluruh berkas setiap satu baris masuk.
/// </summary>
public sealed class ActivityLog(Database db)
{
    /// <summary>
    /// Dipicu setiap satu baris berhasil ditulis.
    ///
    /// Dipakai pendorong panel untuk tahu kapan jejak audit perlu dikirim ulang.
    /// Tanpa ini panel harus menanyakannya berkala tanpa alasan — atau, yang
    /// lebih buruk, hanya menyegarkannya setelah tindakan yang ia lakukan
    /// sendiri, sehingga tindakan panel lain tidak pernah muncul di layarnya.
    /// </summary>
    public event Action? Written;

    public void Write(string actor, string action, string? unitId = null, string? detail = null)
    {
        try
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO activity_log (unit_id, actor, action, detail, created_at)
                VALUES ($unit, $actor, $action, $detail, $now);
                """;
            cmd.Parameters.AddWithValue("$unit", (object?)unitId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$actor", actor);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Log yang gagal ditulis tidak boleh menjatuhkan operasi utama.
            return;
        }

        // Di luar try: kegagalan pada penyimak tidak boleh tersamar sebagai
        // kegagalan menulis log, dan sebaliknya baris yang gagal ditulis tidak
        // boleh memicu panel menggambar sesuatu yang tidak ada di basis data.
        Written?.Invoke();
    }

    public IReadOnlyList<ActivityEntry> Recent(int limit = 50, string? unitId = null)
    {
        var result = new List<ActivityEntry>();
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = unitId is null
            ? "SELECT unit_id, actor, action, detail, created_at FROM activity_log ORDER BY id DESC LIMIT $limit;"
            : "SELECT unit_id, actor, action, detail, created_at FROM activity_log WHERE unit_id = $unit ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        if (unitId is not null) cmd.Parameters.AddWithValue("$unit", unitId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityEntry(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return result;
    }
}
