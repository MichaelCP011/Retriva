using System.Security.Cryptography;
using System.Text;
using Retriva.Server.Data;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>Satu panel admin yang pernah atau sedang tersambung.</summary>
public sealed class PanelState
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; set; }
    /// <summary>
    /// Dapat diterbitkan ulang, bukan init-only.
    ///
    /// Panel di mesin koordinator yang kehilangan panel.json-nya harus dapat
    /// pulih tanpa menghapus basis data — dan pemulihannya berarti token baru
    /// menggantikan yang lama pada baris yang sama.
    /// </summary>
    public required string DeviceToken { get; set; }

    public PanelRole Role { get; set; } = PanelRole.Pending;

    /// <summary>
    /// Wewenangnya pernah ada lalu dicabut.
    ///
    /// Berbeda dari sekadar Pending. Panel Pending adalah orang yang sedang
    /// menunggu jawaban; panel yang dicabut adalah keputusan yang baru saja
    /// diambil asisten. Menampilkan keduanya di daftar yang sama membuat
    /// pencabutan terlihat seolah langsung disusul permohonan masuk kembali.
    /// </summary>
    public bool Revoked { get; set; }

    /// <summary>Peran yang diminta panel. Petunjuk bagi penyetuju, bukan wewenang.</summary>
    public PanelRole RequestedRole { get; set; } = PanelRole.Dosen;

    public string? ConnectionId { get; set; }
    public bool WiredConnection { get; set; }
    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    public bool Online => ConnectionId is not null;
}

/// <summary>
/// Daftar panel admin beserta wewenangnya.
///
/// TIGA ATURAN YANG MENENTUKAN KEAMANANNYA, dan ketiganya sengaja sederhana
/// supaya dapat diperiksa dengan membacanya, bukan dengan memercayainya.
///
/// Pertama, panel baru selalu lahir sebagai Pending. Mengetahui kode pendaftaran
/// tidak membuat siapa pun menjadi Asisten. Kode bisa bocor — tertinggal di
/// papan tulis, terfoto, diteruskan lewat chat — dan bila kode saja sudah cukup,
/// kebocoran sekali berarti kendali penuh atas 40 mesin.
///
/// Kedua, peran diberikan oleh Superadmin, bukan dinyatakan sendiri oleh panel
/// yang mendaftar. Panel hanya mengirim identitas; yang menentukan ia Dosen atau
/// Asisten adalah orang yang menyetujuinya.
///
/// Ketiga, panel pertama dari mesin yang menjalankan koordinator langsung
/// menjadi Superadmin. Tanpa ini tidak ada seorang pun yang dapat menyetujui
/// siapa pun — dan syaratnya berupa akses fisik ke PC Asisten, bukan sekadar
/// pengetahuan yang bisa berpindah tangan.
/// </summary>
public sealed class PanelRegistry
{
    private readonly Dictionary<string, PanelState> _byDeviceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly PanelStore _store;

    public PanelRegistry(PanelStore store)
    {
        _store = store;

        // Memulihkan persetujuan yang tersimpan. Tanpa ini setiap penyalaan
        // ulang koordinator akan mengembalikan seluruh laptop dosen ke Pending.
        foreach (var row in store.All())
        {
            _byDeviceId[row.DeviceId] = new PanelState
            {
                DeviceId = row.DeviceId,
                DisplayName = row.DisplayName,
                DeviceToken = row.DeviceToken,
                Role = row.Role,
                Revoked = row.Revoked,
                FirstSeen = row.FirstSeen,
                LastSeen = row.LastSeen,
            };
        }
    }

    /// <summary>Dipicu setiap ada perubahan, dipakai panel untuk menggambar ulang.</summary>
    public event Action? Changed;

    /// <summary>
    /// Mendaftarkan atau menyambungkan kembali sebuah panel.
    ///
    /// <paramref name="fromLoopback"/> hanya boleh true bila sambungannya
    /// benar-benar berasal dari mesin yang sama. Nilainya diambil dari alamat
    /// remote milik koneksi, bukan dari apa pun yang dikirim panel — kalau
    /// panel yang menyatakannya sendiri, siapa pun bisa mengaku lokal.
    /// </summary>
    public PanelWelcome Register(PanelHello hello, string connectionId, bool fromLoopback,
                                 PanelEnrollment enrollment, string thumbprint, string labName)
    {
        lock (_gate)
        {
            if (_byDeviceId.TryGetValue(hello.DeviceId, out var known))
            {
                /*
                    PANEL LOOPBACK TIDAK PERNAH DITOLAK KARENA TOKEN.

                    Ini bukan kelonggaran, melainkan syarat agar sistemnya dapat
                    diperbaiki sama sekali. Panel di mesin koordinator adalah
                    SATU-SATUNYA yang dapat menyetujui panel lain dan membuka
                    pendaftaran. Kalau ia dapat terkunci di luar, tidak ada
                    seorang pun yang berwenang memperbaikinya — dan jalan
                    keluarnya hanya menghapus basis data lewat Explorer.

                    Token bisa tidak cocok karena sebab yang sepenuhnya wajar:
                    panel.json terhapus, mesin dipasang ulang, atau folder data
                    berpindah sementara barisnya di basis data tertinggal.
                    Semuanya berakhir dengan lab yang tidak dapat dikelola.

                    Akses fisik ke mesin koordinator lebih kuat daripada token
                    mana pun — orang yang duduk di depannya sudah dapat membuka
                    berkas basis datanya langsung. Jadi tokennya diperbarui,
                    bukan sambungannya ditolak.
                */
                var tokenMatches = FixedTimeEquals(known.DeviceToken, hello.DeviceToken ?? "");

                /*
                    TIGA KEADAAN, DAN SEBELUMNYA DUA DI ANTARANYA TERTUKAR.

                    Panel yang KEHILANGAN tokennya — dilupakan, panel.json
                    terhapus, laptop dipasang ulang — datang tanpa membawa
                    apa-apa. Panel yang MEMALSUKAN token datang membawa nilai
                    yang salah. Keduanya dulu ditolak dengan kalimat yang sama,
                    padahal yang pertama justru keadaan yang paling wajar
                    terjadi dan satu-satunya jalan keluarnya adalah menghapus
                    basis data Superadmin.

                    Gejalanya persis yang terjadi kemarin: dosen melepas tautan,
                    ingin bergabung lagi, dan ditolak selamanya. Mencabut
                    wewenangnya pun tidak menolong — pencabutan mengubah peran,
                    bukan token, sehingga penolakannya tetap terjadi sebelum
                    peran sempat diperiksa.

                    Sekarang: datang TANPA token selagi pendaftaran dibuka
                    berarti mendaftar ulang. Barisnya dipulihkan dengan token
                    baru dan dikembalikan ke Pending — jadi tidak ada wewenang
                    yang diwarisi, dan asisten tetap harus menyetujuinya lagi.
                    Membawa token yang SALAH tetap ditolak.
                */
                var lostToken = string.IsNullOrWhiteSpace(hello.DeviceToken);
                var mayReenroll = !fromLoopback && lostToken && enrollment.IsOpen;

                if (!fromLoopback && !tokenMatches && !mayReenroll)
                {
                    return new PanelWelcome
                    {
                        Accepted = false,
                        RejectReason = lostToken
                            ? "Lab sedang tidak menerima panel baru. Minta asisten menekan "
                            + "\"Terima permintaan bergabung\" di PC Asisten."
                            : "Token perangkat tidak cocok dengan yang tercatat di lab ini.",
                    };
                }

                if (fromLoopback) known.Role = PanelRole.Superadmin;

                // Mendaftar ulang berarti mulai dari nol: wewenang lama tidak
                // diwarisi oleh perangkat yang tidak lagi dapat membuktikan
                // dirinya sebagai perangkat yang sama.
                else if (mayReenroll && !tokenMatches)
                {
                    // Mendaftar ulang adalah permohonan yang sungguh-sungguh,
                    // jadi penanda dicabut dilepas — barisnya kembali muncul
                    // di daftar tunggu, tempat yang memang benar untuknya.
                    known.Role = PanelRole.Pending;
                    known.Revoked = false;
                }

                known.ConnectionId = connectionId;
                known.LastSeen = DateTimeOffset.UtcNow;
                known.WiredConnection = hello.WiredConnection;
                known.DisplayName = hello.DisplayName;

                // Permintaan peran diperbarui hanya selama panel masih Pending.
                //
                // Panel yang sudah disetujui sebagai Dosen tidak boleh menaikkan
                // permintaannya menjadi Asisten dan membuat barisnya di layar
                // asisten terbaca seakan ada yang perlu ditinjau ulang. Kalau
                // perannya memang perlu berubah, jalurnya adalah cabut lalu
                // setujui ulang — dan itu terlihat di jejak audit.
                if (known.Role == PanelRole.Pending) known.RequestedRole = hello.RequestedRole;

                // Token baru diterbitkan untuk kedua jalur pemulihan: panel
                // loopback yang kehilangan berkasnya, dan panel jaringan yang
                // mendaftar ulang selagi pendaftaran dibuka.
                string? reissued = null;
                if (!tokenMatches && (fromLoopback || mayReenroll))
                {
                    reissued = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    known.DeviceToken = reissued;
                }

                Persist(known);
                if (reissued is not null) _store.ReissueToken(known.DeviceId, reissued);

                Changed?.Invoke();
                return Welcome(known, labName, issuedToken: reissued, thumbprint: thumbprint);
            }

            /*
                Pendaftaran baru.

                Panel loopback dilewatkan tanpa kode: ia sudah membuktikan
                dirinya lewat akses fisik ke mesin koordinator, yang lebih kuat
                daripada pengetahuan apa pun yang bisa berpindah tangan.

                Panel dari jaringan harus membuktikan ia mengetahui kode yang
                sedang berlaku — dan buktinya terikat pada sertifikat yang
                dilihatnya, sehingga tidak dapat disodorkan ulang oleh penyadap.
            */
            /*
                SATU GERBANG, BUKAN DUA.

                Sebelumnya panel dari jaringan harus membuktikan ia mengetahui
                kode dua belas karakter yang diketik ulang oleh dosen. Kode itu
                membela diri dari "panel asing masuk ke daftar" — padahal
                persetujuan Superadmin sudah membela hal yang sama, dan
                membelanya lebih baik: kode dapat bocor, sedangkan persetujuan
                menuntut seseorang menekan tombol sambil melihat siapa yang
                meminta.

                Yang tersisa hanyalah saklar "menerima permintaan bergabung".
                Tertutup secara bawaan, dibuka asisten selama semenit dua saat
                mendaftarkan laptop dosen. Tidak ada yang perlu diketik oleh
                siapa pun.

                Yang hilang bersama kode itu adalah pengikatan kriptografis
                antara kode dan sertifikat. Penggantinya bersifat manusiawi:
                panel menampilkan kode pendek miliknya sendiri, asisten melihat
                kode yang sama di baris permintaannya. Untuk lab tertutup dengan
                asisten yang berdiri di sebelah dosen, itu memadai — dan ia
                benar-benar dipakai, sedangkan kode dua belas karakter akan
                dilewati orang begitu terasa merepotkan.
            */
            if (!fromLoopback && !enrollment.IsOpen)
            {
                return new PanelWelcome
                {
                    Accepted = false,
                    RejectReason =
                        "Lab sedang tidak menerima panel baru. "
                      + "Minta asisten menekan \"Terima permintaan bergabung\" di PC Asisten.",
                };
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var panel = new PanelState
            {
                DeviceId = hello.DeviceId,
                DisplayName = hello.DisplayName,
                DeviceToken = token,
                ConnectionId = connectionId,
                WiredConnection = hello.WiredConnection,

                // Yang diminta dicatat; yang diberikan ditentukan penyetuju.
                RequestedRole = hello.RequestedRole,

                // Inilah bagian yang menentukan: dari jaringan selalu Pending.
                Role = fromLoopback ? PanelRole.Superadmin : PanelRole.Pending,
            };

            _byDeviceId[hello.DeviceId] = panel;
            Persist(panel);
            Changed?.Invoke();

            // Bukti balik hanya untuk panel jaringan — panel loopback tidak
            // memeriksanya, dan menghitungnya di luar jendela pendaftaran
            // menghasilkan null.
            // Sidik jari dikirim apa adanya, bukan sebagai bukti kriptografis.
            // Panel menampilkannya untuk dibandingkan mata dengan yang tampil
            // di layar Superadmin — perbandingan manusia, dan dinyatakan
            // demikian alih-alih berpura-pura menjadi lebih dari itu.
            return Welcome(panel, labName, token, thumbprint);
        }
    }

    private void Persist(PanelState p) => _store.Upsert(new PanelRecord(
        p.DeviceId, p.DisplayName, p.DeviceToken, p.Role, p.Revoked, p.FirstSeen, p.LastSeen));

    /// <summary>
    /// Membandingkan rahasia dalam waktu tetap.
    ///
    /// string.Equals berhenti pada karakter pertama yang berbeda, sehingga
    /// lamanya perbandingan membocorkan berapa banyak karakter awal yang sudah
    /// benar. Di jaringan lab selisihnya memang tenggelam oleh derau, tetapi
    /// menulisnya benar tidak lebih mahal daripada menulisnya salah.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static PanelWelcome Welcome(PanelState panel, string labName,
                                        string? issuedToken, string? thumbprint) => new()
    {
        Accepted = true,
        IssuedDeviceToken = issuedToken,
        SuperadminProof = thumbprint,
        Role = panel.Role,
        Rights = RightsFor(panel.Role),
        LabName = labName,
    };

    /// <summary>
    /// Wewenang diturunkan dari peran DI SINI, di sisi Superadmin.
    ///
    /// Panel menerima daftar ini semata-mata untuk menyembunyikan tombol yang
    /// tidak boleh dipakai. Penegakannya tetap di setiap metode hub — panel
    /// yang menyembunyikan tombol tanpa pemeriksaan di server hanyalah hiasan,
    /// karena pesannya dapat disusun sendiri oleh siapa pun yang mau.
    /// </summary>
    public static PanelRights RightsFor(PanelRole role) => role switch
    {
        PanelRole.Superadmin => PanelRights.Superadmin,
        PanelRole.Asisten => PanelRights.Asisten,
        PanelRole.Dosen => PanelRights.Dosen,
        _ => PanelRights.None,
    };

    public PanelState? ByConnection(string connectionId)
    {
        lock (_gate)
        {
            return _byDeviceId.Values.FirstOrDefault(p => p.ConnectionId == connectionId);
        }
    }

    public void Disconnect(string connectionId)
    {
        lock (_gate)
        {
            var panel = _byDeviceId.Values.FirstOrDefault(p => p.ConnectionId == connectionId);
            if (panel is null) return;
            panel.ConnectionId = null;
            panel.LastSeen = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke();
    }

    /// <summary>Menyetujui panel yang menunggu, sekaligus menentukan perannya.</summary>
    public bool Approve(string deviceId, PanelRole role)
    {
        if (role is PanelRole.Pending) return false;

        lock (_gate)
        {
            if (!_byDeviceId.TryGetValue(deviceId, out var panel)) return false;
            panel.Role = role;

            // Penanda dicabut dibersihkan: panel ini kembali berwenang, dan
            // membiarkannya tertulis "dicabut" akan menampilkannya sebagai
            // dua keadaan sekaligus di layar yang sama.
            panel.Revoked = false;
            Persist(panel);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Mencabut wewenang, mengembalikan panel ke Pending.
    ///
    /// Tidak menghapusnya dari daftar: panel yang dihapus akan mendaftar lagi
    /// sebagai perangkat baru pada sambungan berikutnya, dan pencabutan yang
    /// dapat dibatalkan dengan menyambung ulang bukan pencabutan.
    /// </summary>
    public bool Revoke(string deviceId)
    {
        lock (_gate)
        {
            if (!_byDeviceId.TryGetValue(deviceId, out var panel)) return false;
            panel.Role = PanelRole.Pending;
            panel.Revoked = true;
            Persist(panel);
        }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<PanelState> Snapshot()
    {
        lock (_gate) return [.. _byDeviceId.Values.OrderBy(p => p.DisplayName)];
    }

    /// <summary>Connection id seluruh panel yang sedang tersambung dan sudah disetujui.</summary>
    public IReadOnlyList<string> ApprovedConnections()
    {
        lock (_gate)
        {
            return [.. _byDeviceId.Values
                .Where(p => p.Online && p.Role != PanelRole.Pending)
                .Select(p => p.ConnectionId!)];
        }
    }
}
