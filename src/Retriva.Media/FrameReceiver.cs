using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Retriva.Shared.Broadcast;

namespace Retriva.Media;

public sealed record ReceivedFrame(byte[] Jpeg, int Width, int Height, uint FrameId);

public sealed record ReceiverStats(
    long FramesComplete, long FramesDropped, long PacketsReceived, long PacketsIgnored)
{
    public double LossPercent =>
        FramesComplete + FramesDropped == 0
            ? 0
            : FramesDropped * 100.0 / (FramesComplete + FramesDropped);
}

/// <summary>
/// Menerima dan merakit ulang frame dari potongan UDP.
///
/// Hanya beberapa frame terakhir yang dirakit sekaligus. Paket bisa datang
/// tidak berurutan, tetapi menyimpan riwayat panjang tidak ada gunanya:
/// begitu frame yang lebih baru selesai, frame lama sudah tidak relevan
/// untuk ditampilkan dan hanya memakan memori.
/// </summary>
public sealed class FrameReceiver : IDisposable
{
    private sealed class Assembly
    {
        public required byte[] Buffer { get; init; }
        public required bool[] Received { get; init; }
        public required int ChunkCount { get; init; }
        public required ushort Width { get; init; }
        public required ushort Height { get; init; }
        public int ReceivedCount;
        public int ActualLength;
    }

    private const int MaxConcurrentFrames = 3;

    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<uint, Assembly> _pending = [];
    private Task? _loop;
    private uint _newestCompleted;

    private long _complete, _dropped, _received, _ignored;

    public event Action<ReceivedFrame>? FrameReady;

    /// <summary>
    /// Keterangan tentang frame yang dibuang sebelum lengkap.
    ///
    /// ADA KARENA "0 frame utuh" TIDAK MENYEBUTKAN APA YANG KURANG.
    ///
    /// Paket tiba berlimpah dan tidak satu frame pun selesai. Dari luar,
    /// keadaan itu punya tiga sebab yang sama sekali berbeda: potongan yang
    /// hilang, panjang frame yang tidak pernah tertetapkan, atau jumlah potongan
    /// yang tidak konsisten antar paket. Ketiganya tampil sebagai angka nol yang
    /// sama, dan menebak di antaranya sudah memakan waktu berjam-jam.
    ///
    /// Dipicu paling banyak sekali per dua detik supaya tidak menenggelamkan
    /// log dengan lima belas baris per detik.
    /// </summary>
    public event Action<string>? Diagnostic;

    private DateTime _lastReport = DateTime.MinValue;

    /// <summary>Port yang benar-benar diikat, untuk dicetak saat menelusuri.</summary>
    public int Port { get; }

    /// <summary>
    /// Galat saat bergabung ke grup multicast, null bila berhasil.
    ///
    /// DICATAT, TIDAK DILEMPAR. Penonton yang gagal bergabung tetap berguna:
    /// siaran unicast masih dapat diterima, dan melempar di sini akan membuat
    /// layar mahasiswa kosong tanpa satu pun keterangan. Tetapi kegagalannya
    /// harus terlihat — bergabung yang gagal diam-diam menghasilkan layar hitam
    /// yang tidak dapat dibedakan dari lab yang memang tidak menyiarkan.
    /// </summary>
    public string? JoinError { get; private set; }

    public ReceiverStats Stats => new(_complete, _dropped, _received, _ignored);

    /// <param name="multicastGroup">
    /// Grup yang diikuti. Null berarti hanya menerima unicast ke port ini.
    /// </param>
    public FrameReceiver(int port = FramePacket.DefaultPort,
                         string? multicastGroup = FramePacket.MulticastGroup)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // ReuseAddress WAJIB diset sebelum Bind. Tanpa ini, dua proses di satu
        // mesin tidak bisa sama-sama mendengarkan grup yang sama — dan itu
        // persis yang terjadi di PC admin, yang menjalankan konsol sekaligus
        // klien untuk memverifikasi siarannya sendiri.
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Buffer terima besar. Satu frame tiba sebagai puluhan paket beruntun;
        // buffer kecil membuat paket dibuang kernel sebelum sempat dibaca,
        // dan gejalanya menyerupai kehilangan paket di jaringan.
        _socket.ReceiveBufferSize = 4 << 20;

        _socket.Bind(new IPEndPoint(IPAddress.Any, port));

        Port = port;

        if (multicastGroup is not null)
        {
            // Bergabung ke grup harus SETELAH Bind. Urutan terbalik gagal diam-diam
            // pada sebagian tumpukan jaringan Windows.
            /*
                BERGABUNG KE SETIAP ANTARMUKA, BUKAN HANYA KE YANG BAWAAN.

                IPAddress.Any pada AddMembership membuat Windows memilih SATU
                antarmuka menurut tabel rute. Di mesin dengan adaptor virtual
                pilihan itu kerap jatuh ke jaringan yang bukan tempat siaran
                dipancarkan, dan hasilnya layar hitam tanpa satu pun galat.

                Pemancar sudah mengirim ke semua antarmuka demi alasan yang
                sama. Penerima harus menyimak dengan cara yang sama pula —
                kalau tidak, keduanya tetap bisa saling melewatkan.
            */
            var group = IPAddress.Parse(multicastGroup);
            var joined = 0;
            var problems = new List<string>();

            foreach (var local in LocalAddresses())
            {
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, local));
                    joined++;
                }
                catch (SocketException ex)
                {
                    problems.Add($"{local}: {ex.SocketErrorCode}");
                }
            }

            if (joined == 0)
            {
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, IPAddress.Any));
                    joined++;
                }
                catch (SocketException ex)
                {
                    problems.Add($"bawaan: {ex.SocketErrorCode}");
                }
            }

            JoinedInterfaces = joined;
            JoinError = joined == 0 && problems.Count > 0 ? string.Join("; ", problems) : null;
            Group = multicastGroup;
        }
    }

    /// <summary>Grup multicast yang diikuti, null bila hanya unicast.</summary>
    public string? Group { get; }

    public void Start() => _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[FramePacket.MaxPacketSize];

        while (!ct.IsCancellationRequested)
        {
            int length;
            try
            {
                length = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            _received++;
            Handle(buffer.AsSpan(0, length));
        }
    }

    private void Handle(ReadOnlySpan<byte> packet)
    {
        if (!FramePacket.TryReadHeader(packet, out var frameId, out var chunkIndex,
                out var chunkCount, out var width, out var height))
        {
            _ignored++;
            return;
        }

        // Frame yang lebih tua dari yang sudah selesai tidak ada gunanya lagi.
        if (frameId <= _newestCompleted && _newestCompleted - frameId < uint.MaxValue / 2)
        {
            _ignored++;
            return;
        }

        if (!_pending.TryGetValue(frameId, out var assembly))
        {
            assembly = new Assembly
            {
                Buffer = new byte[chunkCount * FramePacket.MaxPayloadSize],
                Received = new bool[chunkCount],
                ChunkCount = chunkCount,
                Width = width,
                Height = height,
            };
            _pending[frameId] = assembly;
            PruneOldFrames(frameId);
        }

        if (chunkIndex >= assembly.ChunkCount || assembly.Received[chunkIndex]) return;

        var payload = packet[FramePacket.HeaderSize..];
        payload.CopyTo(assembly.Buffer.AsSpan(chunkIndex * FramePacket.MaxPayloadSize));
        assembly.Received[chunkIndex] = true;
        assembly.ReceivedCount++;

        // Potongan terakhir menentukan panjang sebenarnya; sisanya selalu penuh.
        if (chunkIndex == assembly.ChunkCount - 1)
            assembly.ActualLength = chunkIndex * FramePacket.MaxPayloadSize + payload.Length;

        if (assembly.ReceivedCount != assembly.ChunkCount || assembly.ActualLength == 0) return;

        var jpeg = assembly.Buffer.AsSpan(0, assembly.ActualLength).ToArray();
        _pending.Remove(frameId);
        _newestCompleted = frameId;
        _complete++;

        FrameReady?.Invoke(new ReceivedFrame(jpeg, assembly.Width, assembly.Height, frameId));
    }

    /// <summary>Membuang rakitan yang tidak akan pernah lengkap.</summary>
    private void PruneOldFrames(uint newestFrameId)
    {
        if (_pending.Count <= MaxConcurrentFrames) return;

        foreach (var id in _pending.Keys.Where(k => newestFrameId - k >= MaxConcurrentFrames).ToList())
        {
            var a = _pending[id];
            _pending.Remove(id);
            _dropped++;

            if (Diagnostic is not null && DateTime.UtcNow - _lastReport > TimeSpan.FromSeconds(2))
            {
                _lastReport = DateTime.UtcNow;

                // Indeks yang belum tiba, paling banyak delapan pertama. Pola
                // indeksnya yang menjawab: hanya yang terakhir berarti panjang
                // frame tidak tertetapkan, tersebar berarti kehilangan sungguhan,
                // seluruhnya berarti paketnya masuk ke frame lain.
                var missing = string.Join(",", Enumerable.Range(0, a.ChunkCount)
                    .Where(i => !a.Received[i]).Take(8));

                Diagnostic($"[rakit] frame {id} dibuang · {a.ReceivedCount}/{a.ChunkCount} potongan · "
                         + $"panjang={a.ActualLength} · kurang=[{missing}] · "
                         + $"tertunda={_pending.Count}");
            }
        }
    }

    /// <summary>Jumlah antarmuka tempat grup berhasil diikuti.</summary>
    public int JoinedInterfaces { get; private set; }

    /// <summary>Antarmuka IPv4 yang aktif dan mendukung multicast.</summary>
    private static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}
