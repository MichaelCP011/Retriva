namespace Retriva.Server;

/// <summary>
/// Jendela waktu tempat panel baru boleh masuk ke daftar tunggu.
///
/// SEBUAH SAKLAR, BUKAN SEBUAH KODE.
///
/// Versi sebelumnya menerbitkan kode dua belas karakter yang harus dibacakan
/// dan diketik ulang. Kode itu membela diri dari "panel asing masuk ke daftar"
/// — persis yang sudah dibela oleh persetujuan Superadmin, dan dibela lebih
/// baik: kode dapat bocor, sedangkan persetujuan menuntut seseorang menekan
/// tombol sambil melihat siapa yang meminta.
///
/// Jadi yang tersisa hanya saklarnya. Tertutup secara bawaan — supaya subnet
/// yang ramai tidak mengisi daftar tunggu dengan permintaan yang tidak diminta
/// siapa pun — dan dibuka asisten selama semenit dua saat mendaftarkan laptop.
///
/// Menutup sendiri setelah tenggatnya. Saklar yang harus diingat untuk
/// dimatikan adalah saklar yang akan ditemukan menyala minggu depan.
/// </summary>
public sealed class PanelEnrollment
{
    /// <summary>
    /// Umur jendela.
    ///
    /// Sepuluh menit: cukup untuk berjalan ke meja dosen, membuka aplikasinya,
    /// dan mengulanginya sekali kalau Wi-Fi-nya sedang malas. Tidak cukup untuk
    /// bertahan sampai kelas berikutnya.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private DateTimeOffset _expiresAt;

    public event Action? Changed;

    public bool IsOpen
    {
        get { lock (_gate) return DateTimeOffset.UtcNow < _expiresAt; }
    }

    public DateTimeOffset? ExpiresAt
    {
        get { lock (_gate) return DateTimeOffset.UtcNow < _expiresAt ? _expiresAt : null; }
    }

    public DateTimeOffset Open()
    {
        DateTimeOffset until;
        lock (_gate)
        {
            _expiresAt = DateTimeOffset.UtcNow + Lifetime;
            until = _expiresAt;
        }
        Changed?.Invoke();
        return until;
    }

    public void Close()
    {
        lock (_gate) _expiresAt = default;
        Changed?.Invoke();
    }
}
