using Microsoft.Data.Sqlite;
using Retriva.Shared.Protocol;

namespace Retriva.Server.Data;

/// <summary>
/// Sesi ujian dan peristiwa yang tercatat selama sesi berjalan.
///
/// SATU SESI AKTIF PADA SATU WAKTU, ditegakkan di sini dan bukan di panel.
///
/// Panel mana pun yang punya hak ExamMode dapat memulai sesi, dan di lab ini
/// ada beberapa panel sekaligus — asisten dan dosen. Dua sesi yang berjalan
/// bersamaan berarti peristiwa yang sama tercatat dua kali dengan dua judul
/// berbeda, dan tidak ada cara memutuskan mana yang benar setelah kejadiannya
/// lewat. Menegakkannya di panel akan menyerahkan aturan itu kepada pihak yang
/// tidak dapat melihat panel lain.
/// </summary>
public sealed class ExamStore(Database db)
{
    /// <summary>
    /// Memulai sesi baru, atau mengembalikan null bila sudah ada yang berjalan.
    ///
    /// Null berarti "ditolak", bukan "gagal". Yang memanggilnya perlu
    /// membedakan keduanya supaya pesan yang muncul di panel menyebutkan sesi
    /// yang sedang berjalan, bukan galat yang tidak dapat ditindaklanjuti.
    /// </summary>
    public ExamSession? Start(string title, string startedBy)
    {
        var clean = string.IsNullOrWhiteSpace(title) ? "Sesi ujian" : title.Trim();

        using var connection = db.Open();
        using var tx = connection.BeginTransaction();

        if (ActiveIn(connection, tx) is not null) return null;

        var now = DateTimeOffset.UtcNow;

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO exam_sessions (title, started_by, started_at)
            VALUES ($title, $by, $now)
            RETURNING id;
            """;
        insert.Parameters.AddWithValue("$title", clean);
        insert.Parameters.AddWithValue("$by", startedBy);
        insert.Parameters.AddWithValue("$now", now.ToString("O"));

        var id = Convert.ToInt64(insert.ExecuteScalar());
        tx.Commit();

        return new ExamSession
        {
            Id = id,
            Title = clean,
            StartedBy = startedBy,
            StartedAt = now,
        };
    }

    /// <summary>Menutup sesi yang berjalan. False bila memang tidak ada.</summary>
    public bool End()
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE exam_sessions SET ended_at = $now
            WHERE ended_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Sesi yang sedang berjalan, null bila tidak ada.</summary>
    public ExamSession? Active()
    {
        using var connection = db.Open();
        return ActiveIn(connection, null);
    }

    private static ExamSession? ActiveIn(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT s.id, s.title, s.started_by, s.started_at,
                   (SELECT COUNT(*) FROM exam_events e WHERE e.session_id = s.id)
            FROM exam_sessions s
            WHERE s.ended_at IS NULL
            ORDER BY s.id DESC
            LIMIT 1;
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ExamSession
        {
            Id = reader.GetInt64(0),
            Title = reader.GetString(1),
            StartedBy = reader.GetString(2),
            StartedAt = DateTimeOffset.Parse(reader.GetString(3)),
            EventCount = reader.GetInt32(4),
        };
    }

    /// <summary>
    /// Mencatat satu peristiwa ke sesi yang berjalan.
    ///
    /// Peristiwa yang datang saat tidak ada sesi DIBUANG, dan itu disengaja.
    /// Klien dapat mengirimkannya sesaat setelah sesi ditutup — pesan yang sudah
    /// di jalan, atau pengamatan yang sedang disusun. Menyimpannya tanpa sesi
    /// berarti basis data berisi catatan pengawasan dari waktu ketika tidak ada
    /// yang diberi tahu bahwa ia diawasi.
    ///
    /// Mengembalikan false supaya yang memanggilnya dapat mencatat bahwa ini
    /// terjadi, bukan mengira semuanya tersimpan.
    /// </summary>
    public bool Record(ExamEvent evt)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();

        if (ActiveIn(connection, tx) is not { } session) return false;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO exam_events (session_id, unit_id, kind, description, detected_at)
            VALUES ($session, $unit, $kind, $desc, $now);
            """;
        cmd.Parameters.AddWithValue("$session", session.Id);
        cmd.Parameters.AddWithValue("$unit", evt.UnitId);
        cmd.Parameters.AddWithValue("$kind", evt.Kind.ToString());

        // Dipotong di sini, bukan dipercayakan ke klien. Judul jendela dapat
        // sepanjang apa pun, dan satu unit yang bermasalah mampu menumbuhkan
        // basis data lebih cepat daripada yang akan diperhatikan siapa pun.
        cmd.Parameters.AddWithValue("$desc", Clamp(evt.Description, 400));
        cmd.Parameters.AddWithValue("$now", evt.DetectedAt.ToString("O"));
        cmd.ExecuteNonQuery();

        tx.Commit();
        return true;
    }

    /// <summary>Lini masa satu unit dalam sebuah sesi, urut waktu.</summary>
    public ExamEvent[] Timeline(long sessionId, string unitId, int limit = 500)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT unit_id, kind, description, detected_at
            FROM exam_events
            WHERE session_id = $session AND unit_id = $unit
            ORDER BY detected_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$unit", unitId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ExamEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ExamEvent
            {
                UnitId = reader.GetString(0),
                Kind = Enum.TryParse<ExamEventKind>(reader.GetString(1), out var k)
                    ? k
                    : ExamEventKind.Window,
                Description = reader.GetString(2),
                DetectedAt = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }
        return [.. list];
    }

    /// <summary>
    /// Jumlah peristiwa DAN peristiwa terakhir tiap unit, dalam satu pertanyaan.
    ///
    /// DAFTAR YANG HANYA BERISI ANGKA MEMAKSA MENGKLIK EMPAT PULUH KALI.
    ///
    /// Versi sebelumnya hanya mengembalikan hitungan. Asisten yang melihat
    /// "PC-07 · 23" tidak tahu apa pun tentang dua puluh tiga peristiwa itu —
    /// bisa berpindah antar dua jendela sepanjang ujian, bisa juga membuka
    /// sesuatu yang perlu dihampiri. Satu-satunya cara mengetahuinya adalah
    /// mengklik unit itu, membaca, lalu kembali dan mengulanginya untuk unit
    /// berikutnya, empat puluh kali, selagi ujian berjalan.
    ///
    /// Keterangan terakhir mengubah daftar itu menjadi sesuatu yang dapat
    /// DIPINDAI. "PC-07 · membuka YouTube" dapat dibaca sekilas, dan lini masa
    /// lengkapnya tetap ada satu klik di sebelahnya untuk unit yang memang
    /// perlu ditelusuri.
    ///
    /// Fungsi jendela dipakai supaya ini tetap satu pertanyaan. Empat puluh
    /// pertanyaan terpisah tiap kali layar disegarkan akan membebani basis data
    /// asisten yang pada saat bersamaan menerima peristiwa dari empat puluh
    /// klien.
    /// </summary>
    public Dictionary<string, ExamUnitSummary> Summaries(long sessionId)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT unit_id, kind, description, detected_at, n FROM (
                SELECT unit_id, kind, description, detected_at,
                       COUNT(*)     OVER (PARTITION BY unit_id) AS n,
                       ROW_NUMBER() OVER (PARTITION BY unit_id
                                          ORDER BY detected_at DESC, id DESC) AS rn
                FROM exam_events
                WHERE session_id = $session)
            WHERE rn = 1;
            """;
        cmd.Parameters.AddWithValue("$session", sessionId);

        var map = new Dictionary<string, ExamUnitSummary>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = new ExamUnitSummary
            {
                UnitId = reader.GetString(0),
                Count = reader.GetInt32(4),
                LastKind = Enum.TryParse<ExamEventKind>(reader.GetString(1), out var k)
                    ? k
                    : ExamEventKind.Window,
                LastDescription = reader.GetString(2),
                LastSeenAt = DateTimeOffset.Parse(reader.GetString(3)),
            };
        }
        return map;
    }

    /// <summary>Jumlah peristiwa per unit dalam sebuah sesi, untuk daftar ringkas.</summary>
    public Dictionary<string, int> Counts(long sessionId)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT unit_id, COUNT(*) FROM exam_events
            WHERE session_id = $session
            GROUP BY unit_id;
            """;
        cmd.Parameters.AddWithValue("$session", sessionId);

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) map[reader.GetString(0)] = reader.GetInt32(1);
        return map;
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
