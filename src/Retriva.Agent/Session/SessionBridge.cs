using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Retriva.Shared.Local;
using Retriva.Shared.Protocol;

namespace Retriva.Agent.Session;

/// <summary>
/// Sisi agent dari jembatan lintas sesi. Agent yang menjadi server karena
/// ia hidup sejak boot dan tidak pernah mati; klien datang dan pergi
/// mengikuti login pengguna, jadi klien yang menyambung.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionBridge(ILogger log) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new();

    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// StreamWriter tidak aman dipakai banyak thread. Penulisan datang dari
    /// dua tempat sekaligus: loop penerimaan koneksi, dan callback SignalR
    /// yang meneruskan perintah dari konsol.
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public bool ClientConnected => _pipe?.IsConnected == true;
    public string? WindowsUser { get; private set; }
    public string? StudentNim { get; private set; }
    public string? StudentName { get; private set; }

    /// <summary>Perangkat pribadi mahasiswa, bukan PC lab.</summary>
    public bool StudentIsGuest { get; private set; }

    /// <summary>
    /// Frame siaran yang benar-benar dirakit utuh di klien.
    ///
    /// Dilaporkan klien, bukan disimpulkan agent. Agent hanya meneruskan
    /// perintah membuka penonton; ia tidak tahu apakah satu frame pun tiba.
    /// </summary>
    public long BroadcastFrames { get; private set; }

    /// <summary>Paket siaran yang tiba. Nol berarti multicast tidak sampai.</summary>
    public long BroadcastPackets { get; private set; }

    /// <summary>Balasan klien atas perintah, diteruskan agent ke konsol.</summary>
    public event Action<ClientAck>? AckReceived;

    // Nama event sengaja dibedakan dari nama record. Bila sama, "case StudentSignedOut:"
    // di dalam switch akan dibaca compiler sebagai pola konstanta yang menunjuk field
    // event ini, bukan sebagai pola tipe — dan pesan errornya sama sekali tidak menyinggung itu.
    public event Action<StudentSignedIn>? StudentSignInReceived;
    public event Action? StudentSignOutReceived;
    public event Action<bool>? ConnectionChanged;

    /// <summary>Sesi kendali berakhir dari sisi mahasiswa, bukan atas perintah admin.</summary>
    public event Action<string>? RemoteEndedReceived;

    public event Action<StudentChat>? ChatReceived;
    public event Action<bool>? HelpReceived;

    /// <summary>Nama unit yang diketik mahasiswa. Kosong berarti dihapus.</summary>
    public event Action<string>? SelfNameReceived;

    /// <summary>Pengamatan dari klien selama sesi ujian berjalan.</summary>
    public event Action<ExamObservation>? ExamObserved;

    /// <summary>
    /// Aturan akses terakhir yang diketahui koordinator.
    ///
    /// Disimpan di sini, bukan hanya diteruskan, karena klien dapat menyambung
    /// ulang kapan saja — ditutup mahasiswa, dimulai ulang setelah logoff.
    /// Klien yang menyambung ulang harus menerima aturan yang berlaku, bukan
    /// nilai bawaan yang kebetulan sama di kebanyakan lab.
    /// </summary>
    public AccessPolicy Policy { get; private set; } = new();

    /// <summary>
    /// Folder tujuan berkas, disetel AgentWorker dari konfigurasi yang terbaca.
    /// Diteruskan ke klien di pesan sambutan.
    /// </summary>
    public string FileInbox { get; set; } = "";

    /// <summary>Memperbarui aturan dan meneruskannya bila klien sedang tersambung.</summary>
    public async Task SetPolicyAsync(AccessPolicy policy)
    {
        Policy = policy;
        await SendAsync(new ApplyAccessPolicy { Policy = policy });
    }

    public void Start(string unitId, Func<bool> consoleConnected)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(unitId, consoleConnected, _cts.Token));
    }

    /// <summary>
    /// Menerima satu klien pada satu waktu. Lab hanya punya satu sesi
    /// interaktif per PC, jadi klien kedua berarti yang lama sudah mati.
    /// </summary>
    private async Task AcceptLoopAsync(string unitId, Func<bool> consoleConnected, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = CreatePipe();
                log.LogInformation("Menunggu RetrivaClient pada pipa {Pipe}", LocalProtocol.PipeName);

                await _pipe.WaitForConnectionAsync(ct);

                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                ConnectionChanged?.Invoke(true);
                log.LogInformation("RetrivaClient tersambung");

                // Loop baca dijalankan LEBIH DULU, baru pesan pertama dikirim.
                //
                // Bila kedua sisi sama-sama menulis sebelum membaca, keduanya
                // menunggu pihak lain mengosongkan pipa dan tidak ada yang
                // bergerak. Deadlock ini tidak memunculkan error apa pun —
                // koneksi tampak sehat, hanya tidak ada data yang mengalir.
                var readTask = ReadLoopAsync(_pipe, ct);

                await SendAsync(new AgentWelcome
                {
                    UnitId = unitId,
                    ConsoleConnected = consoleConnected(),
                    Policy = Policy,
                    FileInbox = FileInbox,
                });

                await readTask;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Jembatan sesi terputus: {Message}", ex.Message);
            }
            finally
            {
                WindowsUser = null;
                StudentNim = null;

                // Angka siaran dinolkan bersama klien yang pergi. Dibiarkan,
                // panel akan terus melaporkan unit ini menerima siaran padahal
                // tidak ada lagi yang menampilkannya.
                BroadcastFrames = 0;
                BroadcastPackets = 0;
                StudentName = null;
                ConnectionChanged?.Invoke(false);
                _writer = null;
                if (_pipe is not null) await _pipe.DisposeAsync();
                _pipe = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Pipa dibuat SYSTEM tetapi harus dapat dibuka proses di sesi pengguna,
    /// jadi ACL-nya diberikan eksplisit. Tanpa ini klien akan selalu
    /// ditolak dengan UnauthorizedAccessException.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            LocalProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            // Buffer eksplisit, bukan 0. Dengan buffer memadai, penulisan kecil
            // selesai seketika tanpa menunggu pihak lain membaca.
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) { log.LogWarning("Pipa ditutup klien"); return; }
            if (line.Length == 0) continue;
            log.LogInformation("<- klien ({Bytes} byte): {Payload}", line.Length, line);

            ClientMessage? message;
            try { message = JsonSerializer.Deserialize<ClientMessage>(line, Json); }
            catch (JsonException ex)
            {
                log.LogWarning("Pesan klien tidak dikenali: {Message}", ex.Message);
                continue;
            }

            switch (message)
            {
                case ClientHello hello:
                    WindowsUser = hello.WindowsUser;
                    log.LogInformation("Klien sesi {Session} milik {User} (versi {Version})",
                        hello.SessionId, hello.WindowsUser, hello.ClientVersion);
                    break;

                case ClientAck ack:
                    AckReceived?.Invoke(ack);
                    break;

                case StudentSignedIn signIn:
                    StudentNim = signIn.Nim;
                    StudentName = signIn.DisplayName;
                    StudentIsGuest = signIn.IsGuest;
                    log.LogInformation("Mahasiswa masuk: {Name} ({Nim})",
                        signIn.DisplayName, signIn.IsGuest ? "tamu" : signIn.Nim);
                    StudentSignInReceived?.Invoke(signIn);
                    break;

                case StudentSignedOut:
                    StudentNim = null;
                    StudentName = null;
                    StudentIsGuest = false;
                    StudentSignOutReceived?.Invoke();
                    break;

                case RemoteSessionEnded ended:
                    log.LogInformation("Sesi kendali berakhir: {Reason}", ended.Reason);
                    RemoteEndedReceived?.Invoke(ended.Reason);
                    break;

                case StudentChat chat:
                    ChatReceived?.Invoke(chat);
                    break;

                case BroadcastStats stats:
                    BroadcastFrames = stats.FramesComplete;
                    BroadcastPackets = stats.PacketsReceived;
                    break;

                case ExamObservation observation:
                    // Dicatat di log agent juga, bukan hanya diteruskan.
                    // Koordinator bisa saja sedang terputus, dan pengamatan yang
                    // hilang tanpa jejak di kedua sisi tidak dapat ditelusuri.
                    log.LogInformation("Ujian · {Kind}: {Description}",
                        observation.Kind, observation.Description);
                    ExamObserved?.Invoke(observation);
                    break;

                case StudentHelp help:
                    log.LogInformation("Minta bantuan: {State}", help.Raised ? "diangkat" : "dibatalkan");
                    HelpReceived?.Invoke(help.Raised);
                    break;

                case UnitSelfName self:
                    log.LogInformation("Nama unit dari mahasiswa: {Name}",
                        string.IsNullOrWhiteSpace(self.Name) ? "(dihapus)" : self.Name);
                    SelfNameReceived?.Invoke(self.Name);
                    break;
            }
        }
    }

    public async Task<bool> SendAsync(SessionMessage message)
    {
        var writer = _writer;
        if (writer is null)
        {
            log.LogWarning("Tidak dapat mengirim {Type}: belum ada klien", message.GetType().Name);
            return false;
        }
        if (_pipe?.IsConnected != true)
        {
            log.LogWarning("Tidak dapat mengirim {Type}: pipa tidak tersambung", message.GetType().Name);
            return false;
        }

        await _writeGate.WaitAsync();
        try
        {
            var payload = JsonSerializer.Serialize(message, Json);
            await writer.WriteLineAsync(payload);
            log.LogInformation("-> klien: {Payload}", payload);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log.LogWarning("Gagal mengirim ke klien: {Type} — {Message}",
                ex.GetType().Name, ex.Message);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (_pipe is not null) await _pipe.DisposeAsync();
        _cts?.Dispose();
        _writeGate.Dispose();
    }
}
