using Retriva.Shared.Protocol;

namespace Retriva.Server.Data;

/// <summary>Baris panel sebagaimana tersimpan.</summary>
public sealed record PanelRecord(
    string DeviceId, string DisplayName, string DeviceToken,
    PanelRole Role, bool Revoked, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>
/// Menyimpan panel admin beserta perannya.
///
/// Alasan menyimpannya, bukan sekadar mengingatnya selama proses hidup:
/// persetujuan yang hilang setiap koordinator dinyalakan ulang berarti asisten
/// harus menyetujui ulang setiap laptop dosen setiap pagi. Yang terjadi
/// berikutnya mudah ditebak — seseorang akan mencari cara mematikan
/// pemeriksaannya karena mengganggu, dan pemeriksaan yang dimatikan tidak
/// menjaga apa pun.
///
/// Peran disimpan sebagai teks, bukan angka. Angka membuat file basis data
/// tidak dapat dibaca tanpa membuka kode sumbernya, dan menyisipkan satu nilai
/// baru di tengah enum diam-diam menaikkan peran semua orang yang tersimpan
/// setelahnya. Teks tidak punya masalah itu.
/// </summary>
public sealed class PanelStore(Database db)
{
    public IReadOnlyList<PanelRecord> All()
    {
        var result = new List<PanelRecord>();

        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, display_name, device_token, role, revoked, first_seen, last_seen
            FROM panels;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // Peran yang tidak dikenali jatuh ke Pending, bukan dilewati.
            //
            // Dilewati berarti panelnya menghilang dari daftar dan mendaftar
            // lagi sebagai perangkat baru — dengan token baru — sehingga baris
            // lama menggantung selamanya. Pending membuatnya tetap terlihat
            // oleh asisten, yang dapat memutuskan sendiri.
            var role = Enum.TryParse<PanelRole>(reader.GetString(3), out var parsed)
                ? parsed
                : PanelRole.Pending;

            result.Add(new PanelRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                role,
                reader.GetInt32(4) != 0,
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return result;
    }

    public void Upsert(PanelRecord panel)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO panels (device_id, display_name, device_token, role, revoked, first_seen, last_seen)
            VALUES ($id, $name, $token, $role, $revoked, $first, $last)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = excluded.display_name,
                role         = excluded.role,
                revoked      = excluded.revoked,
                last_seen    = excluded.last_seen;
            """;

        // device_token dan first_seen sengaja TIDAK ikut diperbarui.
        //
        // Token yang tertimpa berarti panel dapat menerbitkan ulang tokennya
        // sendiri hanya dengan menyambung kembali, dan pemeriksaan token
        // berhenti membuktikan apa pun. first_seen dipertahankan karena itulah
        // yang dipakai asisten untuk mengenali panel yang muncul tiba-tiba.
        cmd.Parameters.AddWithValue("$id", panel.DeviceId);
        cmd.Parameters.AddWithValue("$name", panel.DisplayName);
        cmd.Parameters.AddWithValue("$token", panel.DeviceToken);
        cmd.Parameters.AddWithValue("$role", panel.Role.ToString());
        cmd.Parameters.AddWithValue("$revoked", panel.Revoked ? 1 : 0);
        cmd.Parameters.AddWithValue("$first", panel.FirstSeen.ToString("O"));
        cmd.Parameters.AddWithValue("$last", panel.LastSeen.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Menerbitkan ulang token sebuah panel.
    ///
    /// Metode terpisah, bukan bagian dari Upsert, dan itu disengaja. Upsert
    /// dipanggil pada setiap sambungan; kalau ia ikut menimpa token, panel mana
    /// pun dapat menerbitkan ulang tokennya sendiri hanya dengan menyambung
    /// kembali, dan pemeriksaan token berhenti membuktikan apa pun.
    ///
    /// Penerbitan ulang hanya sah untuk panel di mesin koordinator, dan
    /// pemanggilnya yang memastikan itu — lihat PanelRegistry.Register.
    /// </summary>
    public void ReissueToken(string deviceId, string token)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE panels SET device_token = $token WHERE device_id = $id;";
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }
}
