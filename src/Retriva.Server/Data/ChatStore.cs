using Microsoft.Data.Sqlite;
using Retriva.Shared.Protocol;

namespace Retriva.Server.Data;

/// <summary>
/// Riwayat percakapan antara admin dan mahasiswa.
///
/// Disimpan permanen, bukan hanya di memori. Dua alasannya berbeda dan keduanya
/// penting: percakapan harus selamat saat panel admin ditutup di tengah sesi
/// praktikum, dan saat ujian isi percakapan adalah bagian dari jejak audit —
/// pertanyaan yang diajukan mahasiswa dan jawaban yang diberikan asisten bisa
/// jadi yang menentukan apakah sebuah tuduhan menyontek beralasan atau tidak.
/// </summary>
public sealed class ChatStore(Database db)
{
    /// <summary>
    /// Menyimpan satu pesan.
    ///
    /// message_id punya UNIQUE constraint, jadi pesan yang sama tidak akan
    /// tercatat dua kali bila agent mengirim ulang setelah menyambung kembali.
    /// Penolakannya ditelan, bukan dilaporkan sebagai kegagalan — duplikat yang
    /// ditolak justru berarti constraint-nya bekerja.
    /// </summary>
    public void Append(ChatMessage message)
    {
        try
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO chat_messages
                    (message_id, unit_id, from_admin, display_name, body, sent_at)
                VALUES ($id, $unit, $admin, $name, $body, $at);
                """;
            cmd.Parameters.AddWithValue("$id", message.MessageId.ToString());
            cmd.Parameters.AddWithValue("$unit", message.UnitId);
            cmd.Parameters.AddWithValue("$admin", message.FromAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("$name", message.DisplayName);
            cmd.Parameters.AddWithValue("$body", message.Text);
            cmd.Parameters.AddWithValue("$at", message.SentAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Pesan yang gagal disimpan tidak boleh menghalangi pengirimannya.
        }
    }

    /// <summary>
    /// Percakapan satu unit, urut dari yang paling lama.
    ///
    /// Diambil terbalik lalu dibalik lagi supaya LIMIT memotong pesan TERTUA,
    /// bukan yang terbaru. Diurutkan menaik langsung, "50 pertama" berarti 50
    /// pesan dari awal semester dan yang baru justru tidak terlihat.
    /// </summary>
    public IReadOnlyList<ChatMessage> History(string unitId, int limit = 100)
    {
        var result = new List<ChatMessage>();

        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT message_id, unit_id, from_admin, display_name, body, sent_at
            FROM chat_messages
            WHERE unit_id = $unit
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$unit", unitId);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ChatMessage
            {
                MessageId = Guid.TryParse(reader.GetString(0), out var id) ? id : Guid.NewGuid(),
                UnitId = reader.GetString(1),
                FromAdmin = reader.GetInt32(2) != 0,
                DisplayName = reader.GetString(3),
                Text = reader.GetString(4),
                SentAt = DateTimeOffset.TryParse(reader.GetString(5), out var at)
                    ? at
                    : DateTimeOffset.UtcNow,
            });
        }

        result.Reverse();
        return result;
    }

    /// <summary>
    /// Jumlah pesan mahasiswa yang belum dibaca, per unit.
    ///
    /// Dipakai grid dashboard untuk menandai unit yang menunggu jawaban. Satu
    /// kueri untuk seluruh unit, bukan satu kueri per unit — dengan 40 unit
    /// yang kedua berarti 40 perjalanan ke basis data setiap kali dashboard
    /// digambar ulang.
    /// </summary>
    public Dictionary<string, int> UnreadByUnit()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT unit_id, COUNT(*)
            FROM chat_messages
            WHERE from_admin = 0 AND read_at IS NULL
            GROUP BY unit_id;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    /// Menghapus seluruh percakapan, mengembalikan jumlah baris yang terhapus.
    ///
    /// Benar-benar DELETE, bukan penanda "disembunyikan". Alasannya kerahasiaan:
    /// siklus praktikum dua jam berganti kelas, dan percakapan yang hanya
    /// disembunyikan masih ada di berkas basis data yang bisa dibuka siapa pun
    /// yang punya akses ke PC admin.
    /// </summary>
    public int ClearAll()
    {
        try
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chat_messages;";
            return cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { return 0; }
    }

    /// <summary>Menandai seluruh pesan masuk dari satu unit sebagai sudah dibaca.</summary>
    public void MarkRead(string unitId)
    {
        try
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE chat_messages SET read_at = $now
                WHERE unit_id = $unit AND from_admin = 0 AND read_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$unit", unitId);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* tidak fatal */ }
    }
}
