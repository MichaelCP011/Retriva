using System.Net;
using Microsoft.Extensions.Logging;

namespace Retriva.Server;

/// <summary>
/// Mencatat antarmuka mana yang dipakai agent untuk menjangkau koordinator.
///
/// MURNI DIAGNOSTIK SEKARANG, dan itu penurunan peran yang disengaja.
///
/// Kelas ini semula dipakai relay untuk memilih antarmuka pemancar multicast.
/// Gagasannya masuk akal — unit berada di tempat agent berada — tetapi runtuh
/// saat pengujian di satu mesin: agent menyambung lewat 127.0.0.1, dan multicast
/// tidak dapat keluar lewat loopback sama sekali. Relay lalu tidak punya
/// antarmuka untuk dipilih dan jatuh ke bawaan sistem, yang keliru dengan cara
/// yang sama sekali tidak terlihat.
///
/// Relay sekarang memancarkan ke SETIAP antarmuka dan tidak lagi memilih. Yang
/// tersisa di sini hanya nilainya sebagai keterangan: satu baris log yang
/// menyebutkan di jaringan mana unit-unit itu sebenarnya berada, karena
/// pertanyaan itu tetap muncul setiap kali ada yang tidak beres.
///
/// Dipertahankan alih-alih dihapus karena keterangannya berguna. Tetapi
/// namanya dan komentarnya diperbarui — kelas yang mengaku menentukan sesuatu
/// padahal tidak lagi menentukan apa pun adalah jebakan bagi siapa pun yang
/// membacanya nanti.
/// </summary>
public sealed class LabNetwork(ILogger<LabNetwork> log)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, int> _byInterface = [];

    /// <summary>Antarmuka yang paling banyak dipakai agent, untuk ditampilkan.</summary>
    public IPAddress? MulticastInterface
    {
        get
        {
            lock (_gate)
            {
                if (_byInterface.Count == 0) return null;

                return _byInterface.OrderByDescending(kv => kv.Value).First().Key;
            }
        }
    }

    /// <summary>
    /// Mencatat antarmuka lokal tempat seorang agent tersambung.
    ///
    /// Dipanggil pada setiap pendaftaran agent. Nilainya diambil dari koneksi,
    /// bukan dari isi pesan — agent tidak dapat mengetahui alamat koordinator
    /// dari sisinya, dan kalaupun bisa, nilai kiriman tidak dibuktikan apa pun.
    /// </summary>
    public void ObserveAgent(IPAddress? local, string unitId)
    {
        if (local is null) return;

        var normalized = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;

        // Loopback diabaikan. Agent tiruan yang berjalan di mesin koordinator
        // sendiri akan melaporkan 127.0.0.1, dan memancarkan multicast ke
        // loopback gagal dengan "unreachable network" — persis kegagalan yang
        // dicari-cari penyebabnya selama ini.
        if (IPAddress.IsLoopback(normalized)) return;

        bool baru;

        lock (_gate)
        {
            baru = !_byInterface.ContainsKey(normalized);
            _byInterface[normalized] = _byInterface.GetValueOrDefault(normalized) + 1;
        }

        if (baru)
        {
            log.LogInformation(
                "Antarmuka lab dikenali: {Address} (dari pendaftaran {Unit})", normalized, unitId);
        }
    }
}
