using System.IO;
using Microsoft.Data.Sqlite;
using Retriva.Shared;

namespace Retriva.Server.Data;

/// <summary>
/// Berkas basis data konsol. Satu berkas, tanpa server, tanpa instalasi —
/// tetapi dengan transaksi, indeks, dan constraint yang tidak dimiliki JSON.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string? path = null)
    {
        RetrivaPaths.EnsureDataDir();
        var file = path ?? Path.Combine(RetrivaPaths.DataDir, "retriva.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        Migrate();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Migrasi berurutan berbasis nomor versi. Setiap perubahan skema di masa
    /// depan ditambahkan sebagai langkah baru, tidak pernah mengubah yang lama —
    /// supaya lab yang sudah berisi data bisa ikut naik versi tanpa kehilangan apa pun.
    /// </summary>
    private void Migrate()
    {
        using var connection = Open();

        // WAL membuat pembaca tidak saling memblokir dengan penulis. Penting
        // karena UI membaca sementara agent terus menulis heartbeat dan log.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        var current = Convert.ToInt32(
            Scalar(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_version;") ?? 0);

        if (current < 1)
        {
            using var tx = connection.BeginTransaction();

            Execute(connection, """
                CREATE TABLE units (
                    unit_id       TEXT PRIMARY KEY,
                    device_token  TEXT NOT NULL,
                    mac_address   TEXT NOT NULL,
                    host_name     TEXT,
                    ip_address    TEXT,
                    agent_version TEXT,
                    enrolled_at   TEXT NOT NULL,
                    last_seen     TEXT NOT NULL
                );
                """, tx);

            Execute(connection, """
                CREATE TABLE chat_messages (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id   TEXT NOT NULL UNIQUE,
                    unit_id      TEXT NOT NULL,
                    from_admin   INTEGER NOT NULL,
                    display_name TEXT NOT NULL,
                    body         TEXT NOT NULL,
                    sent_at      TEXT NOT NULL,
                    read_at      TEXT
                );
                """, tx);
            Execute(connection, "CREATE INDEX ix_chat_unit ON chat_messages(unit_id, sent_at);", tx);

            // Dua constraint yang menegakkan aturan kontrak kursi di tingkat
            // basis data. Di JSON, aturan ini harus dicek manual dan akan
            // kebobolan cepat atau lambat saat dua admin menulis bersamaan.
            Execute(connection, """
                CREATE TABLE seat_contracts (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    nim          TEXT NOT NULL,
                    student_name TEXT NOT NULL,
                    course       TEXT NOT NULL,
                    class_name   TEXT NOT NULL,
                    semester     TEXT NOT NULL,
                    unit_id      TEXT NOT NULL,
                    created_at   TEXT NOT NULL,
                    UNIQUE (nim, course, semester),
                    UNIQUE (unit_id, course, semester)
                );
                """, tx);

            Execute(connection, """
                CREATE TABLE file_transfers (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_name  TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    sha256     TEXT,
                    unit_id    TEXT NOT NULL,
                    direction  TEXT NOT NULL,
                    status     TEXT NOT NULL,
                    sent_at    TEXT NOT NULL
                );
                """, tx);

            Execute(connection, """
                CREATE TABLE activity_log (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    unit_id    TEXT,
                    actor      TEXT NOT NULL,
                    action     TEXT NOT NULL,
                    detail     TEXT,
                    created_at TEXT NOT NULL
                );
                """, tx);
            Execute(connection, "CREATE INDEX ix_log_created ON activity_log(created_at DESC);", tx);

            Execute(connection, """
                CREATE TABLE testmode_events (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    unit_id     TEXT NOT NULL,
                    description TEXT NOT NULL,
                    severity    TEXT NOT NULL,
                    detected_at TEXT NOT NULL
                );
                """, tx);

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (1);", tx);

            tx.Commit();
        }

        if (current < 2)
        {
            using var tx = connection.BeginTransaction();

            // Alias unit: nama pilihan asisten, misalnya "Laptop Bu Rina".
            // Melekat pada unit_id — yang berasal dari token perangkat — bukan
            // pada alamat IP. Alias yang melekat pada IP akan berpindah orang
            // begitu DHCP memberi alamat yang sama kepada laptop lain, dan
            // asisten akan mengira ia mengunci layar Bu Rina padahal bukan.
            Execute(connection, "ALTER TABLE units ADD COLUMN alias TEXT;", tx);

            // Panel admin yang sudah disetujui.
            //
            // Disimpan, bukan sekadar diingat dalam memori, karena persetujuan
            // yang hilang saat koordinator dinyalakan ulang berarti asisten
            // harus menyetujui ulang setiap laptop dosen setiap pagi. Yang
            // terjadi berikutnya sudah bisa ditebak: seseorang akan mematikan
            // pemeriksaannya supaya tidak mengganggu, dan hilanglah gunanya.
            Execute(connection, """
                CREATE TABLE panels (
                    device_id    TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    device_token TEXT NOT NULL,
                    role         TEXT NOT NULL,
                    first_seen   TEXT NOT NULL,
                    last_seen    TEXT NOT NULL
                );
                """, tx);

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (2);", tx);

            tx.Commit();
        }

        if (current < 3)
        {
            using var tx = connection.BeginTransaction();

            /*
                Membedakan "belum pernah disetujui" dari "wewenangnya dicabut".

                Keduanya berperan Pending, dan itu benar — sama-sama tidak boleh
                melakukan apa pun. Tetapi di layar asisten keduanya sangat
                berbeda: yang pertama adalah orang yang sedang menunggu jawaban,
                yang kedua adalah keputusan yang baru saja diambil asisten
                sendiri.

                Tanpa kolom ini, mencabut wewenang sebuah panel langsung
                memunculkannya kembali di daftar "menunggu persetujuan" — seolah
                ia memohon masuk lagi sedetik setelah dikeluarkan.
            */
            Execute(connection, "ALTER TABLE panels ADD COLUMN revoked INTEGER NOT NULL DEFAULT 0;", tx);

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (3);", tx);

            tx.Commit();
        }

        if (current < 4)
        {
            using var tx = connection.BeginTransaction();

            /*
                Unit yang diblokir.

                Blokir MELEKAT PADA BARIS UNIT, bukan pada daftar alamat IP.
                Alamat berpindah setiap DHCP memberi nomor lain, jadi blokir
                berbasis IP akan mengenai mahasiswa yang salah dalam sehari —
                dan yang dimaksud tetap masuk seperti biasa.

                Disimpan, bukan hanya diingat selama proses hidup: koordinator
                yang dinyalakan ulang tidak boleh melepaskan blokir yang baru
                dipasang pagi itu.
            */
            Execute(connection, "ALTER TABLE units ADD COLUMN blocked INTEGER NOT NULL DEFAULT 0;", tx);

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (4);", tx);

            tx.Commit();
        }

        if (current < 5)
        {
            using var tx = connection.BeginTransaction();

            /*
                Unit yang membuka siaran langsung dalam layar penuh.

                Ini menjawab masalah proyektor. Di kelas, proyektor tercolok ke
                PC dosen, sementara dosen mempresentasikan dari laptopnya sendiri.
                Tanpa penanda ini, dosen harus berjalan ke PC dosen dan membuka
                jendela siaran dengan tangan — atau lebih buruk, memindahkan
                kabel HDMI, yang justru pekerjaan yang ingin dihapus aplikasi ini.

                Melekat pada UNIT, bukan pada setelan global: hanya PC yang
                tercolok proyektor yang boleh mengambil alih layarnya sendiri.
                Layar mahasiswa tidak boleh direbut siaran tanpa diminta.
            */
            Execute(connection,
                "ALTER TABLE units ADD COLUMN auto_fullscreen INTEGER NOT NULL DEFAULT 0;", tx);

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (5);", tx);

            tx.Commit();
        }

        if (current < 6)
        {
            using var tx = connection.BeginTransaction();

            /*
                Sesi ujian, dan peristiwa yang tercatat selama sesi berjalan.

                MENGGANTIKAN testmode_events, YANG TIDAK PERNAH DIPAKAI.

                Tabel itu dibuat pada versi 1 beserta protokol dan hak akses yang
                lengkap — TestModeEvent, IConsoleHub.ReportTestModeEvent,
                PanelRights.ExamMode — dan tidak ada satu baris pun yang pernah
                menulis, membaca, atau menampilkannya. Fitur yang sepenuhnya
                dirancang dan sepenuhnya tidak ada, dengan bentuk yang cukup
                meyakinkan untuk membuat orang mengira ia bekerja.

                Bentuk barunya berbeda dalam dua hal yang disengaja.

                Pertama, ADA SESI. testmode_events hanya punya detected_at, jadi
                catatannya adalah aliran tanpa awal dan akhir: tidak ada cara
                menjawab "apa yang terjadi selama kuis minggu lima" selain
                menebak dari rentang jam. Sekarang setiap peristiwa melekat pada
                sesi bernama.

                Kedua, TIDAK ADA KOLOM 'severity'. Kolom itu ada di tabel lama
                bersama komentar "pelanggaran yang terdeteksi" — dan itu klaim
                yang tidak dapat didukung perangkat lunak ini. Mahasiswa yang
                membuka Notepad saat ujian pemrograman bukan sedang menyontek.
                Menandai peristiwa sebagai pelanggaran berarti Retriva memutuskan
                hal yang seharusnya diputuskan pengajar, dan yang membacanya akan
                memperlakukan label itu sebagai kesimpulan.

                Yang disimpan adalah CATATAN. Penilaiannya milik manusia.
            */
            Execute(connection, """
                CREATE TABLE exam_sessions (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    title      TEXT NOT NULL,
                    started_by TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    ended_at   TEXT
                );
                """, tx);

            Execute(connection, """
                CREATE TABLE exam_events (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id  INTEGER NOT NULL,
                    unit_id     TEXT NOT NULL,
                    kind        TEXT NOT NULL,
                    description TEXT NOT NULL,
                    detected_at TEXT NOT NULL
                );
                """, tx);

            // Lini masa selalu dibaca per unit dalam satu sesi, dan selalu
            // berurutan waktu. Indeksnya mengikuti bentuk pertanyaan itu.
            Execute(connection,
                "CREATE INDEX ix_exam_events ON exam_events(session_id, unit_id, detected_at);", tx);

            /*
                Tabel lama DIBIARKAN, tidak dihapus.

                Ia kosong pada setiap pemasangan yang pernah ada — tidak ada yang
                pernah mengisinya — jadi menghapusnya tidak menghilangkan data
                siapa pun. Tetapi DROP TABLE pada migrasi yang berjalan di mesin
                asisten tanpa pengawasan adalah risiko yang tidak dibayar oleh
                keuntungan apa pun; yang didapat hanya satu baris kosong lebih
                sedikit di basis data.

                UnitStore.Rename masih memperbaruinya, dan itu dibiarkan juga:
                pernyataan UPDATE pada tabel kosong tidak melakukan apa-apa, dan
                menghapusnya berarti menyentuh jalur penggantian nama tanpa
                alasan.
            */

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (6);", tx);

            tx.Commit();
        }

        if (current < 7)
        {
            using var tx = connection.BeginTransaction();

            /*
                Nama yang diusulkan MAHASISWA, disimpan terpisah dari alias.

                Alias adalah milik asisten; ia yang menata denah lab dan
                memutuskan mana PC-01. Nama ini milik orang yang duduk di
                depannya, dan keduanya tidak boleh menimpa satu sama lain.

                Kolom terpisah membuat aturan itu ditegakkan penyimpanannya,
                bukan diingat-ingat oleh kode yang menulis. Satu kolom bersama
                akan membuat mahasiswa yang mengetik "laptop gaming" menghapus
                penamaan yang sudah disusun asisten — dan yang terlihat di panel
                hanyalah nama unit yang berubah sendiri, tanpa jejak siapa yang
                mengubahnya.

                Yang dipakai untuk menampilkan adalah alias bila ada, lalu nama
                ini, lalu unit id. Urutan itu berarti mahasiswa hanya mengisi
                kekosongan, tidak pernah mengambil alih.
            */
            Execute(connection, "ALTER TABLE units ADD COLUMN self_name TEXT;", tx);

            Execute(connection, "DELETE FROM schema_version;", tx);
            Execute(connection, "INSERT INTO schema_version (version) VALUES (7);", tx);

            tx.Commit();
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value == DBNull.Value ? null : value;
    }
}
