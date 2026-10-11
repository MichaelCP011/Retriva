using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Retriva.Shared.Local;
using Retriva.Shared.Protocol;

namespace Retriva.Client;

/// <summary>
/// Sisi klien dari jembatan lintas sesi. Menyambung ke agent dan terus
/// mencoba lagi selama aplikasi hidup — agent mungkin belum siap saat
/// pengguna login, atau layanan sedang diperbarui.
/// </summary>
public sealed class AgentLink : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _loop;

    /// <summary>StreamWriter tidak aman dipakai banyak thread sekaligus.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public bool Connected => _pipe?.IsConnected == true;
    public string? UnitId { get; private set; }

    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Aturan akses yang berlaku, ditentukan koordinator.
    ///
    /// Nilai bawaannya yang paling ketat: login wajib, tamu boleh. Klien yang
    /// belum menerima aturan apa pun tidak boleh melonggarkan sendiri — sesi
    /// tanpa identitas yang muncul karena pesan pertama belum tiba adalah
    /// lubang yang tidak akan pernah tampak di log mana pun.
    /// </summary>
    public AccessPolicy Policy { get; private set; } = new();

    public event Action<AccessPolicy>? PolicyChanged;
    public event Action<string>? Log;

    /// <summary>
    /// Handler WAJIB mengembalikan hasil sesungguhnya, dan hasilnya itulah
    /// yang dikirim sebagai ack ke agent.
    ///
    /// Versi sebelumnya mengirim ack "berhasil" segera setelah pesan diterima,
    /// tanpa peduli apakah overlay benar-benar tampil. Akibatnya panel admin
    /// menyatakan layar terkunci padahal tidak terjadi apa pun — kegagalan
    /// yang menyamar sebagai keberhasilan adalah yang paling mahal dilacak.
    /// </summary>
    public Func<ShowLockScreen, bool>? LockRequested;
    public Func<bool>? UnlockRequested;
    public Func<ShowBroadcast, bool>? BroadcastRequested;

    /// <summary>Diminta menyiarkan layar sendiri ke kelas.</summary>
    public Func<BeginPresenting, bool>? PresentRequested;
    public Action? PresentEndRequested;
    public Func<bool>? BroadcastStopRequested;

    /// <summary>Mengembalikan keterangan bila sesi kendali dimulai, atau null bila gagal.</summary>
    public Func<BeginRemoteSession, string?>? RemoteBeginRequested;

    /// <summary>
    /// Menanyakan izin kepada mahasiswa sebelum kendali dimulai.
    ///
    /// Mengembalikan Task karena jawabannya datang dari manusia, bukan dari
    /// kode. Membuatnya sinkron berarti utas pembaca pipa berhenti selama
    /// mahasiswa berpikir — dan selama itu tidak ada satu pun pesan lain dari
    /// agent yang terbaca, termasuk perintah membatalkan permintaan ini.
    /// </summary>
    public Func<BeginRemoteSession, Task<bool>>? RemoteConsentRequested;

    /// <summary>
    /// Sesi ujian dimulai atau berakhir. Judul kosong berarti berakhir.
    ///
    /// Satu callback untuk kedua arah, bukan dua. Menyalakan pemantauan dan
    /// menampilkan papannya harus terjadi bersamaan — dua jalur terpisah
    /// memungkinkan keadaan yang salah satunya menyala tanpa yang lain, dan
    /// keduanya buruk ke arah yang berlawanan.
    /// </summary>
    public Action<string>? ExamWatchChanged;

    /// <summary>
    /// Nama unit yang tersimpan di mesin ini, dibaca ulang tiap kali menyambung.
    /// Null atau kosong berarti belum pernah diisi dan tidak ada yang dikirim.
    /// </summary>
    public Func<string?>? SelfName;

    /// <summary>Folder tujuan berkas menurut agent, dikabarkan di pesan sambutan.</summary>
    public Action<string>? FileInboxChanged;
    public Func<bool>? RemoteEndRequested;

    /// <summary>
    /// Peristiwa input tidak dibalas dengan ack.
    ///
    /// Gerakan mouse datang puluhan kali per detik; membalas tiap satu berarti
    /// mengirim puluhan pesan per detik kembali melalui pipa dan hub hanya untuk
    /// mengatakan "sudah". Kegagalan suntikan dilaporkan terpisah dan berkala,
    /// bukan per peristiwa.
    /// </summary>
    public Action<InjectInput>? InputRequested;

    public Action<DeliverChat>? ChatArrived;
    public Action<string>? HelpAcknowledgedByAdmin;
    public Action? ChatCleared;
    public Action<FileArrived>? FileArrivedAtClient;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(
                    ".", LocalProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                Log?.Invoke("Menyambung ke RetrivaAgent…");
                await _pipe.ConnectAsync(ct);

                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                ConnectionChanged?.Invoke(true);
                Log?.Invoke("Tersambung ke RetrivaAgent");

                // Loop baca dijalankan LEBIH DULU, baru pesan pertama dikirim.
                // Bila kedua sisi sama-sama menulis sebelum membaca, keduanya
                // menunggu pihak lain dan tidak ada data yang pernah mengalir —
                // tanpa error apa pun, koneksi tampak sehat tetapi mati.
                var readTask = ReadLoopAsync(_pipe, ct);

                await SendAsync(new ClientHello
                {
                    WindowsUser = Environment.UserName,
                    SessionId = Process.GetCurrentProcess().SessionId,
                    ClientVersion = typeof(AgentLink).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    DocumentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                });

                /*
                    Nama unit DIKIRIM ULANG pada tiap sambungan, bukan sekali
                    saat diketik.

                    Mahasiswa mengetiknya sekali dan tidak akan mengetiknya lagi.
                    Bila hanya dikirim saat itu, nama tersebut hilang pada setiap
                    keadaan yang wajar terjadi: agent belum berjalan saat klien
                    dibuka, koordinator sedang mati, basis data lab diganti, atau
                    unitnya mendaftar ulang sebagai unit baru.

                    Yang terlihat di panel adalah nama yang muncul sekali lalu
                    tidak pernah kembali — dan tidak ada seorang pun yang akan
                    menghubungkannya dengan sambungan yang sempat terputus.

                    Mengirimnya ulang tiap sambungan membuatnya menjadi keadaan
                    yang diselaraskan, bukan peristiwa yang bisa terlewat.
                */
                if (SelfName?.Invoke() is { Length: > 0 } saved)
                    await SendAsync(new UnitSelfName { Name = saved });

                await readTask;
            }
            catch (OperationCanceledException) { return; }
            catch (UnauthorizedAccessException)
            {
                // Umumnya berarti pipa dibuat tanpa ACL yang mengizinkan sesi pengguna.
                Log?.Invoke("Akses pipa ditolak — periksa ACL di sisi agent");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Terputus: {ex.Message}");
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
                _writer = null;
                if (_pipe is not null) await _pipe.DisposeAsync();
                _pipe = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(LocalProtocol.ReconnectSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) { Log?.Invoke("pipa ditutup agent"); return; }
            if (line.Length == 0) continue;

            SessionMessage? message;
            try { message = JsonSerializer.Deserialize<SessionMessage>(line, Json); }
            catch (JsonException ex)
            {
                Log?.Invoke($"pesan tidak dikenali: {ex.Message}");
                continue;
            }

            switch (message)
            {
                case AgentWelcome welcome:
                    UnitId = welcome.UnitId;
                    Policy = welcome.Policy;
                    Log?.Invoke($"Terdaftar sebagai {welcome.UnitId} · konsol "
                              + (welcome.ConsoleConnected ? "tersambung" : "terputus")
                              + $" · login {(Policy.RequireStudentLogin ? "wajib" : "tidak wajib")}"
                              + $" · tamu {(Policy.AllowGuestAccess ? "boleh" : "tidak")}");
                    PolicyChanged?.Invoke(Policy);

                    if (!string.IsNullOrWhiteSpace(welcome.FileInbox))
                        FileInboxChanged?.Invoke(welcome.FileInbox);
                    break;

                case BeginExamWatch begin:
                    Log?.Invoke($"sesi ujian dimulai · {begin.Title}");
                    ExamWatchChanged?.Invoke(begin.Title);
                    await AckAsync(begin.CommandId, true, "pemantauan ujian aktif");
                    break;

                case EndExamWatch end:
                    Log?.Invoke("sesi ujian berakhir");
                    ExamWatchChanged?.Invoke("");
                    await AckAsync(end.CommandId, true, "pemantauan ujian berhenti");
                    break;

                case ApplyAccessPolicy apply:
                    Policy = apply.Policy;
                    Log?.Invoke("Aturan akses diperbarui koordinator");
                    PolicyChanged?.Invoke(Policy);
                    await AckAsync(apply.CommandId, true, "aturan akses diterapkan");
                    break;

                case ShowLockScreen show:
                {
                    var ok = LockRequested?.Invoke(show) ?? false;
                    Log?.Invoke(ok ? "overlay kunci tampil" : "overlay kunci GAGAL tampil");
                    await AckAsync(show.CommandId, ok,
                        ok ? "layar terkunci" : "overlay tidak dapat ditampilkan");
                    break;
                }

                case HideLockScreen hide:
                {
                    var ok = UnlockRequested?.Invoke() ?? false;
                    await AckAsync(hide.CommandId, ok,
                        ok ? "kunci dibuka" : "overlay tidak dapat disembunyikan");
                    break;
                }

                case BeginRemoteSession begin when begin.NeedsConsent
                                                  && RemoteConsentRequested is { } ask:
                {
                    /*
                        Permintaan dijawab SEKARANG, keputusannya menyusul.

                        Koordinator menunggu balasan perintah dalam dua belas
                        detik; mahasiswa tidak. Menahan balasan sampai ia
                        menekan tombol berarti panel selalu melaporkan
                        "tidak dijawab" walaupun izinnya kemudian diberikan.

                        Jadi yang dibalas di sini adalah fakta yang memang sudah
                        pasti — permintaannya sampai dan sedang ditampilkan.
                        Penolakan menyusul lewat RemoteSessionEnded, jalur yang
                        sama dengan sesi yang berakhir sebab lain, dan panel
                        sudah menanganinya.
                    */
                    await AckAsync(begin.CommandId, true, "menunggu persetujuan mahasiswa");

                    _ = Task.Run(async () =>
                    {
                        var granted = await ask(begin);

                        if (!granted)
                        {
                            Log?.Invoke("sesi kendali DITOLAK mahasiswa");
                            await SendAsync(new RemoteSessionEnded { Reason = "ditolak mahasiswa" });
                            return;
                        }

                        var started = RemoteBeginRequested?.Invoke(begin);
                        Log?.Invoke(started is null
                            ? "sesi kendali GAGAL dimulai setelah disetujui"
                            : $"sesi kendali dimulai · {started}");

                        if (started is null)
                            await SendAsync(new RemoteSessionEnded { Reason = "gagal memulai di klien" });
                    });
                    break;
                }

                case BeginRemoteSession begin:
                {
                    var detail = RemoteBeginRequested?.Invoke(begin);
                    Log?.Invoke(detail is null
                        ? "sesi kendali GAGAL dimulai"
                        : $"sesi kendali dimulai · {detail}");
                    await AckAsync(begin.CommandId, detail is not null,
                        detail ?? "sesi kendali tidak dapat dimulai");
                    break;
                }

                case EndRemoteSession end:
                {
                    var ok = RemoteEndRequested?.Invoke() ?? false;
                    await AckAsync(end.CommandId, ok,
                        ok ? "sesi kendali diakhiri" : "tidak ada sesi kendali yang berjalan");
                    break;
                }

                case InjectInput inject:
                    InputRequested?.Invoke(inject);
                    break;

                case DeliverChat chat:
                    ChatArrived?.Invoke(chat);
                    await AckAsync(chat.CommandId, true, "pesan ditampilkan");
                    break;

                case FileArrived file:
                    FileArrivedAtClient?.Invoke(file);
                    await AckAsync(file.CommandId, true, "berkas ditampilkan");
                    break;

                case ClearChat clear:
                    ChatCleared?.Invoke();
                    await AckAsync(clear.CommandId, true, "percakapan dikosongkan");
                    break;

                case HelpAcknowledged ack:
                    HelpAcknowledgedByAdmin?.Invoke(ack.By);
                    await AckAsync(ack.CommandId, true, "penanda bantuan dipadamkan");
                    break;

                case BeginPresenting begin:
                {
                    var ok = PresentRequested?.Invoke(begin) ?? false;
                    Log?.Invoke(ok
                        ? $"menyiarkan layar sendiri ke {begin.RelayHost}:{begin.RelayPort}"
                        : "GAGAL menyiarkan layar sendiri");
                    await AckAsync(begin.CommandId, ok,
                        ok ? "presentasi dimulai" : "presentasi tidak dapat dimulai");
                    break;
                }

                case EndPresenting end:
                {
                    PresentEndRequested?.Invoke();
                    Log?.Invoke("presentasi layar sendiri dihentikan");
                    await AckAsync(end.CommandId, true, "presentasi dihentikan");
                    break;
                }

                case ShowBroadcast show:
                {
                    var ok = BroadcastRequested?.Invoke(show) ?? false;
                    Log?.Invoke(ok
                        ? $"penonton siaran dibuka pada {show.MulticastGroup}:{show.Port}"
                        : "penonton siaran GAGAL dibuka");
                    await AckAsync(show.CommandId, ok,
                        ok ? "penonton dibuka" : "penonton tidak dapat dibuka");
                    break;
                }

                case HideBroadcast hide:
                {
                    var ok = BroadcastStopRequested?.Invoke() ?? false;
                    await AckAsync(hide.CommandId, ok, ok ? "penonton ditutup" : "gagal menutup");
                    break;
                }
            }
        }
    }

    public Task AckAsync(Guid commandId, bool success, string? detail = null) =>
        SendAsync(new ClientAck { CommandId = commandId, Success = success, Detail = detail });

    public async Task SendAsync(ClientMessage message)
    {
        var writer = _writer;
        if (writer is null || _pipe?.IsConnected != true)
        {
            Log?.Invoke($"tidak dapat mengirim {message.GetType().Name}: pipa tidak siap");
            return;
        }

        await _writeGate.WaitAsync();
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log?.Invoke($"gagal mengirim: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (_pipe is not null) await _pipe.DisposeAsync();
        _cts.Dispose();
        _writeGate.Dispose();
    }
}
