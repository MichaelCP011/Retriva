using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>Satu unit sebagaimana dilihat konsol saat ini.</summary>
public sealed class UnitState
{
    /// <summary>
    /// Dapat disetel, bukan init-only, semata-mata agar unit dapat diganti
    /// namanya tanpa menunggu agent menyambung ulang. Hanya UnitRegistry.Rename
    /// yang boleh mengubahnya, dan hanya sambil memindahkan kuncinya di kamus —
    /// mengubahnya di tempat lain akan membuat unit tersimpan dengan kunci yang
    /// tidak lagi sama dengan namanya.
    /// </summary>
    public required string UnitId { get; set; }
    public required string ConnectionId { get; set; }
    public string HostName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string AgentVersion { get; set; } = "";

    /// <summary>
    /// Alamat koordinator DILIHAT DARI UNIT INI — alamat tujuan yang dipakai
    /// agent untuk menyambung ke hub.
    ///
    /// Dipakai untuk memberi tahu unit ke mana ia harus mengirim layarnya saat
    /// diminta presentasi. Tidak bisa memakai alamat yang dilihat panel:
    /// koordinator dengan beberapa kartu jaringan punya beberapa alamat, dan
    /// laptop dosen di Wi-Fi bisa menjangkaunya lewat alamat yang sama sekali
    /// tidak terjangkau dari PC mahasiswa yang tersambung kabel.
    ///
    /// Kegagalannya tidak bersuara: unit mengirim ke alamat yang tidak ada,
    /// relay tidak menerima apa pun, dan tidak ada yang melaporkan kesalahan.
    /// </summary>
    public string CoordinatorHost { get; set; } = "";

    /// <summary>Kabel atau nirkabel, sebagaimana diukur di unit itu sendiri.</summary>
    public bool WiredConnection { get; set; }

    /// <summary>Kecepatan tautan Mbps, 0 bila adapter tidak melaporkannya.</summary>
    public int LinkSpeedMbps { get; set; }

    public string NetworkAdapter { get; set; } = "";

    /// <summary>
    /// Waktu pulang-pergi terakhir dalam milidetik, null bila belum atau tidak
    /// terukur.
    ///
    /// Null berarti "tidak tahu", bukan nol. Nol adalah angka yang tampak
    /// seperti hasil pengukuran terbaik yang mungkin, dan unit yang berhenti
    /// menjawab akan terlihat paling sehat di seluruh peta.
    /// </summary>
    public double? LatencyMs { get; set; }
    public UnitStatus Status { get; set; } = UnitStatus.Offline;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Pratinjau terakhir sebagai data URL siap pakai oleh UI.</summary>
    public string? ThumbnailDataUrl { get; set; }
    public DateTimeOffset? ThumbnailAt { get; set; }

    /// <summary>
    /// Kapan mahasiswa menekan minta bantuan, null bila tidak sedang meminta.
    ///
    /// Sengaja disimpan sebagai waktu, bukan bendera. Asisten perlu tahu SUDAH
    /// BERAPA LAMA seseorang menunggu — dengan bendera, sepuluh unit yang
    /// menyala terlihat sama mendesaknya padahal satu di antaranya sudah
    /// menunggu delapan menit.
    /// </summary>
    public DateTimeOffset? HelpRequestedAt { get; set; }

    /// <summary>Siapa yang sedang memakai unit ini, dilaporkan agent tiap denyut.</summary>
    public string? StudentNim { get; set; }
    public string? StudentName { get; set; }
    public bool IsGuest { get; set; }

    /// <summary>
    /// Frame siaran yang sudah dirakit utuh di unit ini.
    ///
    /// Dipakai menjawab "berapa yang menerima siaran saya" — pertanyaan yang
    /// tidak dapat dijawab dari sisi pengirim, karena multicast UDP tidak punya
    /// jalur balik.
    /// </summary>
    public long BroadcastFrames { get; set; }

    public string Summary =>
        $"{UnitId,-10} {Status,-8} {IpAddress,-16} {HostName,-18} terakhir {LastSeen.LocalDateTime:HH:mm:ss}";
}

/// <summary>
/// Daftar unit yang sedang tersambung. Untuk sekarang in-memory saja;
/// persistensi ke SQLite menyusul di M2.4.
/// </summary>
public sealed class UnitRegistry
{
    private readonly Dictionary<string, UnitState> _byUnitId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Dipicu setiap ada perubahan, dipakai UI untuk menggambar ulang.</summary>
    public event Action? Changed;

    public UnitState Upsert(RegisterRequest req, string connectionId, string? coordinatorHost = null)
    {
        UnitState state;
        lock (_gate)
        {
            if (!_byUnitId.TryGetValue(req.UnitId, out var existing))
            {
                existing = new UnitState { UnitId = req.UnitId, ConnectionId = connectionId };
                _byUnitId[req.UnitId] = existing;
            }

            existing.ConnectionId = connectionId;
            existing.HostName = req.HostName;
            existing.IpAddress = req.IpAddress;
            existing.MacAddress = req.MacAddress;
            existing.AgentVersion = req.AgentVersion;
            existing.WiredConnection = req.WiredConnection;
            existing.LinkSpeedMbps = req.LinkSpeedMbps;
            existing.NetworkAdapter = req.NetworkAdapter;
            if (!string.IsNullOrWhiteSpace(coordinatorHost))
                existing.CoordinatorHost = coordinatorHost;
            existing.Status = UnitStatus.Online;
            existing.LastSeen = DateTimeOffset.UtcNow;
            state = existing;
        }

        Changed?.Invoke();
        return state;
    }

    public void ApplyHeartbeat(HeartbeatReport report)
    {
        lock (_gate)
        {
            if (!_byUnitId.TryGetValue(report.UnitId, out var unit)) return;
            unit.Status = report.Status;
            unit.LastSeen = report.SentAt;

            /*
                Isi denyut DISIMPAN, bukan dibaca lalu dibuang.

                Sebelumnya hanya Status dan SentAt yang dipakai. Agent mengirim
                nama pengguna Windows dan NIM mahasiswa setiap beberapa detik,
                dan keduanya berhenti di baris ini — sementara UnitSnapshot punya
                medan StudentNim dan StudentName yang tidak pernah terisi.
                Datanya sudah sampai di koordinator dan dibuang satu baris
                sebelum menjadi berguna, jadi asisten melihat grid berisi PC-01
                sampai PC-40 tanpa satu pun nama.
            */
            unit.StudentNim = report.RetrivaUserNim;
            unit.StudentName = report.RetrivaUserName;
            unit.IsGuest = report.IsGuest;
            unit.BroadcastFrames = report.BroadcastFrames;
        }
        Changed?.Invoke();
    }

    public void MarkDisconnected(string connectionId)
    {
        lock (_gate)
        {
            var unit = _byUnitId.Values.FirstOrDefault(u => u.ConnectionId == connectionId);
            if (unit is null) return;
            unit.Status = UnitStatus.Offline;
            unit.LastSeen = DateTimeOffset.UtcNow;

            // Latensi ikut dilupakan. Angka terakhir dari unit yang sudah mati
            // akan tampil di peta sebagai jaringan yang sehat.
            unit.LatencyMs = null;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Menandai satu unit offline berdasarkan unit id.
    ///
    /// Terpisah dari MarkDisconnected, yang mencari lewat connection id karena
    /// dipanggil dari OnDisconnectedAsync — di sana unit id-nya belum diketahui.
    /// Di sini yang diketahui justru sebaliknya: asisten memblokir sebuah unit,
    /// dan koneksinya yang harus ditemukan.
    ///
    /// Barisnya TIDAK dihapus dari registri. Unit yang hilang begitu saja dari
    /// grid tidak dapat dibedakan dari PC yang dimatikan, dan asisten kehilangan
    /// satu-satunya tempat untuk melepas blokirnya kembali.
    /// </summary>
    public bool MarkOffline(string unitId)
    {
        lock (_gate)
        {
            if (!_byUnitId.TryGetValue(unitId, out var unit)) return false;
            unit.Status = UnitStatus.Offline;
            unit.LastSeen = DateTimeOffset.UtcNow;

            // Latensi ikut dilupakan. Angka terakhir dari unit yang sudah mati
            // akan tampil di peta sebagai jaringan yang sehat.
            unit.LatencyMs = null;
        }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<UnitState> Snapshot()
    {
        lock (_gate)
        {
            return _byUnitId.Values
                .OrderBy(u => u.UnitId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Menyimpan pratinjau terbaru sebuah unit.
    /// Tidak memicu event Changed: pratinjau didorong ke UI satu per satu
    /// begitu tiba, bukan lewat snapshot penuh, supaya 40 unit yang mengirim
    /// gambar tiap dua detik tidak memaksa seluruh dashboard digambar ulang.
    /// </summary>
    /// <summary>
    /// Menyalakan atau memadamkan penanda minta bantuan.
    /// Mengembalikan true bila keadaannya benar-benar berubah, supaya UI tidak
    /// digambar ulang oleh permintaan yang mengulang keadaan yang sama.
    /// </summary>
    public bool SetHelpRequested(string unitId, bool raised)
    {
        lock (_gate)
        {
            if (!_byUnitId.TryGetValue(unitId, out var unit)) return false;

            var already = unit.HelpRequestedAt is not null;
            if (already == raised) return false;

            unit.HelpRequestedAt = raised ? DateTimeOffset.UtcNow : null;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Memadamkan seluruh penanda minta bantuan sekaligus.</summary>
    public void ClearAllHelp()
    {
        lock (_gate)
        {
            foreach (var unit in _byUnitId.Values) unit.HelpRequestedAt = null;
        }
        Changed?.Invoke();
    }

    public bool StoreThumbnail(string unitId, string dataUrl)
    {
        lock (_gate)
        {
            if (!_byUnitId.TryGetValue(unitId, out var unit)) return false;
            unit.ThumbnailDataUrl = dataUrl;
            unit.ThumbnailAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Menandai offline unit yang berhenti mengirim heartbeat.
    ///
    /// Perlu terpisah dari peristiwa disconnect SignalR karena PC yang mati
    /// mendadak — listrik padam atau kabel dicabut — tidak sempat menutup
    /// koneksinya dengan rapi, sehingga server bisa lama tidak menyadarinya.
    /// </summary>
    /// <returns>True bila ada unit yang berubah statusnya.</returns>
    public bool SweepStale(TimeSpan timeout)
    {
        var changed = false;
        var cutoff = DateTimeOffset.UtcNow - timeout;

        lock (_gate)
        {
            foreach (var unit in _byUnitId.Values)
            {
                if (unit.Status == UnitStatus.Offline || unit.LastSeen >= cutoff) continue;
                unit.Status = UnitStatus.Offline;
                unit.LatencyMs = null;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Connection id unit tertentu, atau null bila sedang tidak tersambung.</summary>
    /// <summary>
    /// Mencatat hasil ukur latensi.
    ///
    /// TIDAK memicu Changed. Pengukuran berjalan tiap lima belas detik untuk
    /// empat puluh unit; menyiarkan seluruh snapshot pada setiap hasil berarti
    /// empat puluh siaran per putaran, hanya karena sebuah angka bergeser
    /// setengah milidetik. Nilainya ikut pada siaran berikutnya yang memang
    /// perlu terjadi.
    /// </summary>
    public void RecordLatency(string unitId, double milliseconds)
    {
        lock (_gate)
        {
            if (_byUnitId.TryGetValue(unitId, out var unit)) unit.LatencyMs = milliseconds;
        }
    }

    public void ClearLatency(string unitId)
    {
        lock (_gate)
        {
            if (_byUnitId.TryGetValue(unitId, out var unit)) unit.LatencyMs = null;
        }
    }

    /// <summary>Alamat koordinator yang terbukti terjangkau dari unit ini.</summary>
    public string? CoordinatorHostFor(string unitId)
    {
        lock (_gate)
        {
            return _byUnitId.TryGetValue(unitId, out var unit)
                   && !string.IsNullOrWhiteSpace(unit.CoordinatorHost)
                ? unit.CoordinatorHost
                : null;
        }
    }

    /// <summary>
    /// Mengganti nama unit di registri yang berjalan, tanpa menunggu ia
    /// menyambung ulang.
    ///
    /// Tanpa ini, nama baru hanya ada di basis data sementara grid, perintah,
    /// dan percakapan masih memakai yang lama sampai agent kebetulan
    /// menyambung ulang. Asisten akan melihat penggantian nama yang "tidak
    /// jadi", lalu mengulanginya — dan yang kedua ditolak karena nama itu
    /// sudah terpakai di basis data.
    /// </summary>
    public bool Rename(string unitId, string newUnitId)
    {
        lock (_gate)
        {
            if (!_byUnitId.TryGetValue(unitId, out var unit)) return false;
            if (_byUnitId.ContainsKey(newUnitId)) return false;

            _byUnitId.Remove(unitId);
            unit.UnitId = newUnitId;
            _byUnitId[newUnitId] = unit;
        }

        Changed?.Invoke();
        return true;
    }

    public string? ConnectionIdFor(string unitId)
    {
        lock (_gate)
        {
            return _byUnitId.TryGetValue(unitId, out var unit) && unit.Status != UnitStatus.Offline
                ? unit.ConnectionId
                : null;
        }
    }

    public int OnlineCount()
    {
        lock (_gate) return _byUnitId.Values.Count(u => u.Status != UnitStatus.Offline);
    }
}
