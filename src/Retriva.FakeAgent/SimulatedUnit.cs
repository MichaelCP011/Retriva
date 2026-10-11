using Microsoft.AspNetCore.SignalR.Client;
using Retriva.Shared.Protocol;
using Retriva.Shared.Security;

namespace Retriva.FakeAgent;

/// <summary>
/// Satu unit mahasiswa tiruan. Bicara protokol yang sama persis dengan agent
/// sungguhan, tetapi tidak melakukan capture, injeksi input, atau kontrol daya.
/// </summary>
internal sealed class SimulatedUnit(string unitId, SimulationOptions options, Stats stats)
{
    private HubConnection? _connection;
    private string? _deviceToken;

    public string UnitId => unitId;
    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>
    /// Status yang dilaporkan. Dibuat bervariasi supaya dashboard menampilkan
    /// campuran aktif, terkunci, dan alert seperti kondisi kelas sesungguhnya.
    /// </summary>
    public UnitStatus ReportedStatus { get; private set; } = UnitStatus.Online;

    /// <summary>Kondisi kunci tiruan, supaya perintah massal dapat diuji pada 40 unit.</summary>
    private bool _locked;

    private string TokenFile =>
        Path.Combine(Path.GetTempPath(), $"retriva-fake-{unitId}.token");

    public async Task RunAsync(CancellationToken ct)
    {
        AssignStatus();
        _deviceToken = ReadTokenOrNull();

        var url = $"https://{options.Host}:{options.Port}{RetrivaProtocol.HubPath}";

        _connection = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http)
                        http.ServerCertificateCustomValidationCallback =
                            (_, cert, _, _) => CertificatePin.Matches(cert, options.Pin);
                    return handler;
                };
                o.WebSocketConfiguration = socket =>
                    socket.RemoteCertificateValidationCallback =
                        (_, cert, _, _) => CertificatePin.Matches(cert, options.Pin);
            })
            .WithAutomaticReconnect(new JitteredRetryPolicy())
            .Build();

        _connection.On<AgentCommand>("Execute", HandleCommandAsync);

        _connection.Reconnecting += _ =>
        {
            stats.Reconnecting();
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            stats.Reconnected();
            await RegisterAsync(ct);
        };

        await _connection.StartAsync(ct);
        if (!await RegisterAsync(ct)) return;

        _ = Task.Run(() => PublishThumbnailsAsync(ct), ct);

        while (!ct.IsCancellationRequested && _connection.State != HubConnectionState.Disconnected)
        {
            if (_connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await _connection.SendAsync("Heartbeat", new HeartbeatReport
                    {
                        UnitId = unitId,
                        Status = _locked ? UnitStatus.Locked : ReportedStatus,
                        LoggedOnUser = "mahasiswa",
                    }, ct);
                    stats.Heartbeat();
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Ditangani SignalR lewat auto-reconnect.
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(RetrivaProtocol.HeartbeatSeconds), ct);
        }
    }

    /// <summary>
    /// Mengirim pratinjau sintetis. Tidak ada layar untuk ditangkap di sini,
    /// jadi gambarnya dibuat: pola warna yang berubah pelan supaya terlihat
    /// bahwa grid benar-benar hidup dan tiap unit berbeda satu sama lain.
    /// </summary>
    private async Task PublishThumbnailsAsync(CancellationToken ct)
    {
        var frame = 0;
        while (!ct.IsCancellationRequested
               && _connection is { State: not HubConnectionState.Disconnected })
        {
            if (_connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await _connection.SendAsync("PublishThumbnail", new ThumbnailFrame
                    {
                        UnitId = unitId,
                        JpegData = SyntheticThumbnail.Create(unitId, frame++),
                        Width = SyntheticThumbnail.Width,
                        Height = SyntheticThumbnail.Height,
                    }, ct);
                }
                catch (Exception) { /* koneksi sedang pulih */ }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>Memutus paksa lalu menyambung lagi — dipakai mode chaos.</summary>
    public async Task BounceAsync(CancellationToken ct)
    {
        if (_connection is null) return;
        try
        {
            await _connection.StopAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(3, 10)), ct);
            await _connection.StartAsync(ct);
            await RegisterAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Line($"[{unitId}] gagal bangkit kembali: {ex.Message}");
        }
    }

    private async Task<bool> RegisterAsync(CancellationToken ct)
    {
        if (_connection is null) return false;

        var response = await _connection.InvokeAsync<RegisterResponse>("Register", new RegisterRequest
        {
            UnitId = unitId,
            HostName = $"LAB-{unitId}",
            IpAddress = FakeIp(),
            MacAddress = FakeMac(),
            AgentVersion = "1.0.0-fake",
            EnrollmentSecret = string.IsNullOrWhiteSpace(_deviceToken) ? options.Secret : null,
            DeviceToken = _deviceToken,
        }, ct);

        if (!response.Accepted)
        {
            Log.Line($"[{unitId}] DITOLAK — {response.RejectReason}");
            stats.Rejected();
            return false;
        }

        if (!string.IsNullOrWhiteSpace(response.IssuedDeviceToken))
        {
            _deviceToken = response.IssuedDeviceToken;
            TryWriteToken(_deviceToken);
        }

        stats.Registered();
        return true;
    }

    private async Task HandleCommandAsync(AgentCommand command)
    {
        if (_connection is null) return;

        // Unit tiruan menjawab perintah daya dan kunci seolah berhasil, supaya
        // perilaku penyebaran ke 40 unit dapat diuji tanpa mematikan atau
        // mengunci apa pun. Yang tidak ditiru tetap dijawab Unsupported —
        // simulator tidak boleh berpura-pura bisa melakukan segalanya, karena
        // itu akan menyembunyikan fitur yang sebenarnya belum ada.
        var supported = command is PingCommand or ShutdownCommand or RestartCommand
                                or AbortShutdownCommand or LockScreenCommand or UnlockScreenCommand;
        stats.CommandReceived(command.GetType().Name);

        if (command is LockScreenCommand) _locked = true;
        if (command is UnlockScreenCommand) _locked = false;

        try
        {
            await _connection.SendAsync("ReportCommandResult", new CommandResult
            {
                CommandId = command.CommandId,
                UnitId = unitId,
                Outcome = supported ? CommandOutcome.Success : CommandOutcome.Unsupported,
                Detail = command switch
                {
                    PingCommand           => $"pong dari {unitId}",
                    ShutdownCommand c     => $"disimulasikan: shutdown dalam {c.DelaySeconds} detik",
                    RestartCommand c      => $"disimulasikan: restart dalam {c.DelaySeconds} detik",
                    AbortShutdownCommand  => "disimulasikan: hitung mundur dibatalkan",
                    LockScreenCommand     => "disimulasikan: layar terkunci",
                    UnlockScreenCommand   => "disimulasikan: kunci dibuka",
                    _                     => $"{command.GetType().Name} diabaikan oleh unit tiruan",
                },
            });
        }
        catch (Exception) { /* koneksi sedang pulih */ }
    }

    private void AssignStatus()
    {
        if (!int.TryParse(unitId.AsSpan(unitId.LastIndexOf('-') + 1), out var n)) return;
        ReportedStatus = n % 7 == 0 ? UnitStatus.Locked
                       : n == 13 || n == 27 ? UnitStatus.Alert
                       : UnitStatus.Online;
    }

    private string FakeIp() =>
        int.TryParse(unitId.AsSpan(unitId.LastIndexOf('-') + 1), out var n)
            ? $"192.168.1.{n}" : "192.168.1.0";

    private string FakeMac() =>
        int.TryParse(unitId.AsSpan(unitId.LastIndexOf('-') + 1), out var n)
            ? $"AABBCCDDEE{n:X2}" : "AABBCCDDEE00";

    private string? ReadTokenOrNull()
    {
        try { return File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null; }
        catch (IOException) { return null; }
    }

    private void TryWriteToken(string token)
    {
        try { File.WriteAllText(TokenFile, token); }
        catch (IOException) { /* tidak fatal */ }
    }
}
