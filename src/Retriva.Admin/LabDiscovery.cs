using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Retriva.Shared.Protocol;

namespace Retriva.Admin;

/// <summary>Sebuah lab yang terdengar di jaringan.</summary>
public sealed record FoundLab(
    string LabName, string Host, int Port, bool AcceptingPanels,
    string? Thumbprint, DateTimeOffset LastSeen);

/// <summary>
/// Mendengarkan beacon lab dan menyimpan yang terdengar.
///
/// Menghilangkan pengetikan alamat IP dari sisi dosen. Yang tersisa untuk
/// manusia hanya memilih lab yang benar bila kebetulan ada lebih dari satu —
/// dan itu keputusan yang memang perlu dilihat orang, bukan ditebak aplikasi.
/// </summary>
public sealed class LabDiscovery : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, FoundLab> _labs = [];
    private readonly Lock _gate = new();
    private readonly ILogger _log;

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;

    public event Action? Changed;

    public LabDiscovery(ILogger log) => _log = log;

    public IReadOnlyList<FoundLab> Labs
    {
        get
        {
            lock (_gate)
            {
                // Yang sudah lewat tenggat dibuang saat dibaca, bukan lewat
                // timer tersendiri. Daftar ini hanya dibaca ketika layar
                // penautan terbuka, jadi timer yang berdenyut sepanjang hidup
                // aplikasi akan bekerja untuk tidak seorang pun.
                var cutoff = DateTimeOffset.UtcNow - PanelProtocol.BeaconTimeout;
                return [.. _labs.Values.Where(l => l.LastSeen >= cutoff)
                                       .OrderBy(l => l.LabName)];
            }
        }
    }

    public void Start()
    {
        if (_socket is not null) return;

        try
        {
            var socket = new UdpClient(AddressFamily.InterNetwork);

            // Alamat boleh dipakai bersama: di mesin pengembangan, Superadmin
            // dan panel Dosen berjalan berdampingan dan keduanya menyentuh
            // port yang sama. Tanpa ini yang kedua gagal mengikat dan penemuan
            // lab diam-diam tidak pernah bekerja di satu-satunya tempat yang
            // dipakai untuk mengujinya.
            socket.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, PanelProtocol.DiscoveryPort));
            socket.JoinMulticastGroup(IPAddress.Parse(PanelProtocol.DiscoveryGroup));

            _socket = socket;
            _cts = new CancellationTokenSource();
            _ = ListenAsync(socket, _cts.Token);

            _log.LogInformation("Mendengarkan beacon lab di {Group}:{Port}",
                PanelProtocol.DiscoveryGroup, PanelProtocol.DiscoveryPort);
        }
        catch (SocketException ex)
        {
            // Penemuan otomatis adalah kemudahan, bukan syarat. Kegagalannya
            // tidak boleh menghalangi penautan manual — tetapi tetap dicatat,
            // karena "labnya tidak muncul" akan ditanyakan orang.
            _log.LogWarning("Penemuan lab tidak dapat dimulai: {Message}", ex.Message);
        }
    }

    private async Task ListenAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await socket.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            LabBeacon? beacon;

            try
            {
                beacon = JsonSerializer.Deserialize<LabBeacon>(
                    Encoding.UTF8.GetString(result.Buffer), Json);
            }
            catch (JsonException)
            {
                // Datagram asing di grup yang sama. Diabaikan tanpa ribut.
                continue;
            }

            if (beacon is null || beacon.Port <= 0) continue;

            /*
                Alamat diambil dari PENGIRIM datagram, bukan dari isinya.

                Mesin dengan beberapa kartu jaringan tidak dapat mengetahui
                lewat mana ia terlihat oleh panel ini — VPN, adaptor virtual,
                dan kartu kedua semuanya punya alamat yang benar dari sudut
                pandangnya sendiri. Alamat pengirim adalah satu-satunya yang
                dijamin merupakan jalur yang benar-benar sampai.
            */
            var host = result.RemoteEndPoint.Address.ToString();
            var key = $"{host}:{beacon.Port}";

            lock (_gate)
            {
                _labs[key] = new FoundLab(
                    beacon.LabName, host, beacon.Port,
                    beacon.AcceptingPanels, beacon.Thumbprint, DateTimeOffset.UtcNow);
            }

            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket?.Dispose();
        _cts?.Dispose();
        _socket = null;
        _cts = null;
    }
}
