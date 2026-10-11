using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Retriva.Shared;
using Retriva.Shared.Protocol;

namespace Retriva.Server.Data;

/// <summary>
/// Hasil pemeriksaan pendaftaran.
///
/// MacMismatch dipertahankan meski tidak lagi pernah dikembalikan. Nilainya
/// mungkin masih ada di jejak aktivitas lama, dan menghapusnya dari enum akan
/// membuat baris-baris itu tidak dapat dibaca kembali. Lihat catatan panjang di
/// Validate untuk alasan MAC berhenti menolak.
/// </summary>
public enum EnrollmentResult { Accepted, BadSecret, TokenMismatch, MacMismatch, Blocked }

/// <summary>
/// Registri unit yang berhak menyambung, beserta token perangkatnya.
/// Menggantikan devices.json — token adalah data, bukan konfigurasi,
/// jadi tempatnya di basis data bersama sisa data lain.
/// </summary>
public sealed class UnitStore
{
    private readonly Database _db;

    /// <summary>
    /// MAC sebuah unit berubah: unitId, MAC lama, MAC baru.
    ///
    /// Peristiwa, bukan penolakan. Yang mendengarkannya menuliskannya ke jejak
    /// aktivitas supaya perubahan tetap terlihat oleh asisten yang memang
    /// mencurigainya — tanpa mengunci PC yang kartu jaringannya sekadar diganti.
    /// </summary>
    public event Action<string, string, string>? MacChanged;

    public UnitStore(Database db)
    {
        _db = db;
        MigrateLegacyJsonIfPresent();
    }

    /// <summary>
    /// Memvalidasi permintaan pendaftaran.
    /// Token perangkat diutamakan; kode pendaftaran hanya untuk sambungan
    /// pertama, sehingga bocornya installer di kemudian hari tidak membuat
    /// orang bisa menyamar sebagai unit yang sudah terdaftar.
    /// </summary>
    /// <param name="proposedUnitId">
    /// Nama yang dihitung koordinator dari alamat IP sambungan, atau null bila
    /// tidak dapat diturunkan. Hanya dipakai untuk unit BARU.
    /// </param>
    /// <returns>
    /// Nama unit yang sah ikut dikembalikan, karena bisa berbeda dari yang
    /// diusulkan agent — lihat catatan panjang di bawah.
    /// </returns>
    public (EnrollmentResult Result, string? Token, string UnitId) Validate(
        RegisterRequest request, string enrollmentSecret, string? proposedUnitId = null)
    {
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();

        /*
            IDENTITAS MELEKAT PADA TOKEN, BUKAN PADA NAMA.

            Unit yang membawa token dicari LEWAT TOKENNYA, dan nama yang
            tersimpan di baris itu yang berlaku — bukan nama yang dikirim agent.

            Tanpa ini, penamaan oleh koordinator menjadi jebakan: agent
            mendaftar sebagai LAB-PC-07, koordinator menjawab "kamu PC-07",
            agent memakai nama baru itu, dan pada sambungan berikutnya barisnya
            dicari dengan nama PC-07 yang tidak ada di basis data. Hasilnya
            "token tidak cocok" selamanya, pada unit yang tidak melakukan apa pun
            yang salah.

            Sekaligus menutup masalah yang lebih lama: alamat IP berpindah setiap
            DHCP memberi nomor baru, dan nama yang diturunkan dari IP ikut
            berpindah. Token tidak berpindah.
        */
        var byToken = string.IsNullOrWhiteSpace(request.DeviceToken)
            ? null
            : FindByToken(connection, tx, request.DeviceToken);

        var unitId = byToken?.UnitId ?? request.UnitId;

        var known = byToken is { } b ? (b.Token, b.Mac) : Find(connection, tx, unitId);

        /*
            BLOKIR DIPERIKSA PALING AWAL, sebelum token maupun kode pendaftaran.

            Unit yang diblokir tidak boleh punya jalan masuk apa pun — termasuk
            mendaftar ulang sebagai perangkat baru dengan kode pendaftaran yang
            sah. Kalau pemeriksaannya diletakkan setelah jalur token, mahasiswa
            yang diblokir tinggal menghapus agent.json dan masuk kembali sebagai
            unit yang belum dikenal.

            Diperiksa dari unit id, yang berasal dari token perangkat dan tetap
            melekat pada mesin itu — bukan dari alamat IP yang berpindah tiap
            kali DHCP memberi nomor lain.
        */
        if (IsBlocked(connection, tx, unitId))
            return (EnrollmentResult.Blocked, null, unitId);

        if (!string.IsNullOrWhiteSpace(request.DeviceToken))
        {
            if (known is null || !FixedTimeEquals(known.Value.Token, request.DeviceToken))
                return (EnrollmentResult.TokenMismatch, null, unitId);

            /*
                MAC YANG BERUBAH DICATAT, TIDAK MENOLAK.

                Sebelumnya perbedaan MAC menolak pendaftaran, dengan alasan "IP
                bisa tertukar antar PC tanpa disadari; MAC yang membongkarnya".
                Alasan itu keliru dalam dua hal sekaligus.

                Pertama, MAC tidak membuktikan apa pun. Ia dikirim oleh agent
                sebagai teks biasa dan dapat diubah di Windows lewat properti
                adapter, tanpa hak administrator pada banyak kartu. Siapa pun
                yang sanggup mencuri token perangkat sanggup pula menyalin MAC
                yang tertera di panel. Yang membuktikan identitas adalah token,
                dan token itulah yang sudah cocok saat baris ini tercapai.

                Kedua, MAC memang berubah pada mesin yang tidak melakukan apa
                pun yang salah: kartu jaringan diganti, laptop dipasang ke dok,
                atau unit berpindah dari Wi-Fi ke kabel. Menolaknya berarti
                mengunci PC yang sehat sampai ada yang menghapus barisnya dari
                basis data — dan pesan yang muncul menunjuk ke perangkat keras,
                bukan ke penyebab sesungguhnya.

                Kejadian yang membuktikannya justru berasal dari sini: perbaikan
                pemilihan kartu jaringan membuat agent melaporkan MAC yang benar
                setelah sebelumnya melaporkan MAC kartu virtual. Seluruh unit
                yang sudah terdaftar langsung tertolak — bukan karena ada yang
                mencurigakan, melainkan karena catatannya yang lama salah.

                Jadi perubahannya dicatat, bukan diadili. Baris jejak aktivitas
                membuat MAC yang berpindah tetap terlihat oleh asisten yang
                memang mencurigainya, tanpa mematikan lab yang tidak bersalah.
            */
            var previousMac = known.Value.Mac;
            var macChanged = !string.IsNullOrWhiteSpace(previousMac)
                && !string.Equals(previousMac, request.MacAddress, StringComparison.OrdinalIgnoreCase);

            Touch(connection, tx, request, unitId);
            tx.Commit();

            if (macChanged) MacChanged?.Invoke(unitId, previousMac, request.MacAddress);

            return (EnrollmentResult.Accepted, known.Value.Token, unitId);
        }

        if (string.IsNullOrWhiteSpace(enrollmentSecret)
            || !FixedTimeEquals(enrollmentSecret, request.EnrollmentSecret ?? ""))
            return (EnrollmentResult.BadSecret, null, unitId);

        // Nama koordinator dipakai hanya di sini: saat unit BARU dicatat.
        // Sesudah itu namanya tetap, apa pun yang terjadi pada alamat IP-nya.
        var assigned = proposedUnitId ?? request.UnitId;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Upsert(connection, tx, request, token, assigned);
        tx.Commit();
        return (EnrollmentResult.Accepted, token, assigned);
    }

    public int Count()
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM units;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Alamat MAC per unit — dipakai Wake-on-LAN di M3.2.</summary>
    public Dictionary<string, string> MacAddresses()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT unit_id, mac_address FROM units;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    /// <summary>
    /// Nama pilihan asisten per unit, hanya yang benar-benar diberi nama.
    ///
    /// Unit tanpa alias tidak muncul di kamus ini, bukan muncul dengan nilai
    /// kosong. Pemanggil yang memakai GetValueOrDefault akan mendapat null dan
    /// menampilkan unit id apa adanya — sedangkan alias kosong akan tampil
    /// sebagai baris tanpa nama sama sekali.
    /// </summary>
    public Dictionary<string, string> Aliases()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT unit_id, alias FROM units WHERE alias IS NOT NULL AND alias <> '';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    /// <summary>Nama yang diketik sendiri mahasiswa di unitnya, hanya yang terisi.</summary>
    public Dictionary<string, string> SelfNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT unit_id, self_name FROM units WHERE self_name IS NOT NULL AND self_name <> '';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    /// <summary>
    /// Menyimpan nama yang diketik mahasiswa. TIDAK menyentuh alias.
    ///
    /// Dipotong pada 40 karakter dan dibersihkan dari baris baru. Medan ini
    /// diisi dari sisi klien, yaitu tempat yang tidak dikendalikan siapa pun di
    /// lab — dan nama sepanjang seribu karakter akan merusak setiap tabel yang
    /// menampilkannya, di panel yang bahkan tidak ada hubungannya dengan unit
    /// itu.
    /// </summary>
    public bool SetSelfName(string unitId, string? name)
    {
        string? clean = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var kept = name.Trim().Where(c => !char.IsControl(c)).ToArray();
            clean = new string(kept).Trim();

            if (clean.Length > 40) clean = clean[..40].TrimEnd();
            if (clean.Length == 0) clean = null;
        }

        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE units SET self_name = $name WHERE unit_id = $id;";
        cmd.Parameters.AddWithValue("$name", (object?)clean ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", unitId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Memberi atau menghapus alias. Alias kosong disimpan sebagai NULL supaya
    /// "belum pernah diberi nama" dan "namanya dihapus" menjadi keadaan yang
    /// sama — tidak ada gunanya membedakan keduanya, dan membedakannya berarti
    /// setiap pembacaan harus menangani dua bentuk "tidak ada nama".
    /// </summary>
    /// <summary>Apakah unit ini sedang diblokir.</summary>
    private static bool IsBlocked(SqliteConnection connection, SqliteTransaction tx, string unitId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT blocked FROM units WHERE unit_id = $id;";
        cmd.Parameters.AddWithValue("$id", unitId);

        // Unit yang belum tercatat tidak diblokir. Menganggapnya diblokir akan
        // menolak seluruh PC baru yang belum pernah mendaftar.
        return cmd.ExecuteScalar() is long v && v != 0;
    }

    /// <summary>Unit id yang sedang diblokir, untuk ditampilkan di panel.</summary>
    public HashSet<string> Blocked()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT unit_id FROM units WHERE blocked <> 0;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));

        return result;
    }

    /// <summary>
    /// Memasang atau melepas blokir.
    ///
    /// Mengembalikan false bila unitnya tidak dikenal, bukan diam-diam berhasil.
    /// Blokir yang dilaporkan berhasil pada unit yang tidak ada akan membuat
    /// asisten yakin sebuah PC sudah dikunci padahal tidak.
    /// </summary>
    public bool SetBlocked(string unitId, bool blocked)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE units SET blocked = $blocked WHERE unit_id = $id;";
        cmd.Parameters.AddWithValue("$blocked", blocked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", unitId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Unit yang membuka siaran langsung layar penuh — biasanya PC proyektor.</summary>
    public HashSet<string> AutoFullscreen()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT unit_id FROM units WHERE auto_fullscreen <> 0;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));

        return result;
    }

    public bool SetAutoFullscreen(string unitId, bool enabled)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE units SET auto_fullscreen = $v WHERE unit_id = $id;";
        cmd.Parameters.AddWithValue("$v", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", unitId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Mengganti nama unit yang SUDAH terdaftar, beserta seluruh riwayatnya.
    ///
    /// unit_id bukan sekadar label — ia dipakai sebagai rujukan di enam tabel
    /// lain yang tidak punya foreign key untuk menegakkannya. Mengganti nama
    /// hanya di tabel units akan meninggalkan percakapan, riwayat berkas, dan
    /// jejak audit yang menunjuk ke nama yang tidak ada lagi.
    ///
    /// Kegagalannya tidak akan terlihat sebagai kesalahan. Data itu tetap ada,
    /// hanya tidak pernah cocok dengan apa pun, sehingga yang tampak di layar
    /// adalah unit yang riwayatnya mendadak kosong — dan tidak ada yang
    /// menghubungkannya dengan penggantian nama beberapa minggu sebelumnya.
    ///
    /// Seluruhnya dalam satu transaksi. Penggantian nama yang berhenti di
    /// tengah jalan meninggalkan basis data yang separuhnya memakai nama lama.
    /// </summary>
    public (bool Ok, string? Reason) Rename(string unitId, string newUnitId)
    {
        var target = newUnitId.Trim();

        if (string.IsNullOrWhiteSpace(target)) return (false, "Nama unit tidak boleh kosong");
        if (target.Length > 40) return (false, "Nama unit terlalu panjang");
        if (string.Equals(target, unitId, StringComparison.Ordinal)) return (true, null);

        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();

        if (Find(connection, tx, unitId) is null)
            return (false, $"Unit {unitId} tidak ada di basis data");

        // Nama yang sudah dipakai unit lain ditolak, bukan ditimpa. unit_id
        // adalah kunci utama; menimpanya akan menggabungkan dua PC menjadi satu
        // baris dan menghapus salah satu tokennya.
        if (Find(connection, tx, target) is not null)
            return (false, $"Nama {target} sudah dipakai unit lain");

        foreach (var sql in new[]
        {
            "UPDATE units           SET unit_id = $new WHERE unit_id = $old;",
            "UPDATE chat_messages   SET unit_id = $new WHERE unit_id = $old;",
            "UPDATE seat_contracts  SET unit_id = $new WHERE unit_id = $old;",
            "UPDATE file_transfers  SET unit_id = $new WHERE unit_id = $old;",
            "UPDATE activity_log    SET unit_id = $new WHERE unit_id = $old;",
            "UPDATE testmode_events SET unit_id = $new WHERE unit_id = $old;",
        })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$new", target);
            cmd.Parameters.AddWithValue("$old", unitId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return (true, null);
    }

    public void SetAlias(string unitId, string? alias)
    {
        var trimmed = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE units SET alias = $alias WHERE unit_id = $id;";
        cmd.Parameters.AddWithValue("$alias", (object?)trimmed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", unitId);
        cmd.ExecuteNonQuery();
    }

    private static (string Token, string Mac)? Find(SqliteConnection c, SqliteTransaction tx, string unitId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT device_token, mac_address FROM units WHERE unit_id = $id;";
        cmd.Parameters.AddWithValue("$id", unitId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    /// <summary>Mencari unit lewat token perangkatnya. Identitas yang sesungguhnya.</summary>
    private static (string UnitId, string Token, string Mac)? FindByToken(
        SqliteConnection c, SqliteTransaction tx, string token)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT unit_id, device_token, mac_address FROM units WHERE device_token = $token;";
        cmd.Parameters.AddWithValue("$token", token);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static void Upsert(SqliteConnection c, SqliteTransaction tx, RegisterRequest r,
                               string token, string unitId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO units (unit_id, device_token, mac_address, host_name, ip_address,
                               agent_version, enrolled_at, last_seen)
            VALUES ($id, $token, $mac, $host, $ip, $ver, $now, $now)
            ON CONFLICT(unit_id) DO UPDATE SET
                device_token = excluded.device_token,
                mac_address  = excluded.mac_address,
                host_name    = excluded.host_name,
                ip_address   = excluded.ip_address,
                agent_version= excluded.agent_version,
                last_seen    = excluded.last_seen;
            """;
        cmd.Parameters.AddWithValue("$id", unitId);
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$mac", r.MacAddress);
        cmd.Parameters.AddWithValue("$host", r.HostName);
        cmd.Parameters.AddWithValue("$ip", r.IpAddress);
        cmd.Parameters.AddWithValue("$ver", r.AgentVersion);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void Touch(SqliteConnection c, SqliteTransaction tx, RegisterRequest r, string unitId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE units SET host_name = $host, ip_address = $ip, mac_address = $mac,
                             agent_version = $ver, last_seen = $now
            WHERE unit_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", unitId);
        cmd.Parameters.AddWithValue("$host", r.HostName);
        cmd.Parameters.AddWithValue("$ip", r.IpAddress);

        // MAC ikut diperbarui. Dibiarkan, catatan lama yang salah akan bertahan
        // selamanya — dan Wake-on-LAN terus dialamatkan ke kartu yang keliru,
        // diam-diam, karena tidak ada satu pun yang membandingkannya lagi.
        cmd.Parameters.AddWithValue("$mac", r.MacAddress);
        cmd.Parameters.AddWithValue("$ver", r.AgentVersion);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Memindahkan devices.json lama ke basis data, supaya unit yang sudah
    /// terdaftar tidak perlu mendaftar ulang setelah pembaruan.
    /// </summary>
    private void MigrateLegacyJsonIfPresent()
    {
        var legacy = RetrivaPaths.DeviceRegistryFile;
        if (!File.Exists(legacy)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            using var connection = _db.Open();
            using var tx = connection.BeginTransaction();

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var unitId = element.GetProperty("UnitId").GetString();
                var token = element.GetProperty("DeviceToken").GetString();
                var mac = element.TryGetProperty("MacAddress", out var m) ? m.GetString() ?? "" : "";
                if (unitId is null || token is null) continue;

                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO units
                        (unit_id, device_token, mac_address, enrolled_at, last_seen)
                    VALUES ($id, $token, $mac, $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$id", unitId);
                cmd.Parameters.AddWithValue("$token", token);
                cmd.Parameters.AddWithValue("$mac", mac);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            File.Move(legacy, legacy + ".migrated", overwrite: true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or SqliteException)
        {
            // Migrasi gagal bukan alasan menghentikan layanan; unit akan
            // mendaftar ulang memakai kode pendaftaran.
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
