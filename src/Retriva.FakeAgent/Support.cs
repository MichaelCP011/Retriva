namespace Retriva.FakeAgent;

internal sealed class SimulationOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Pin { get; init; }
    public required string Secret { get; init; }
    public required int From { get; init; }
    public required int To { get; init; }
    public required bool Chaos { get; init; }

    public int Count => To - From + 1;
}

/// <summary>Penghitung agregat. 40 unit yang mencatat sendiri-sendiri akan
/// membanjiri terminal, jadi yang ditampilkan hanya ringkasan berkala.</summary>
internal sealed class Stats
{
    private int _registered, _rejected, _heartbeats, _reconnecting, _reconnected, _commands;
    private string _lastCommand = "—";

    public void Registered()   => Interlocked.Increment(ref _registered);
    public void Rejected()     => Interlocked.Increment(ref _rejected);
    public void Heartbeat()    => Interlocked.Increment(ref _heartbeats);
    public void Reconnecting() => Interlocked.Increment(ref _reconnecting);
    public void Reconnected()  => Interlocked.Increment(ref _reconnected);

    public void CommandReceived(string name)
    {
        Interlocked.Increment(ref _commands);
        _lastCommand = name;
    }

    public string Summary(int connected, int total) =>
        $"{connected}/{total} tersambung · daftar {_registered} · tolak {_rejected} · "
      + $"heartbeat {_heartbeats} · reconnect {_reconnected} · perintah {_commands} ({_lastCommand})";
}

/// <summary>Penulisan terminal yang aman dipakai banyak task sekaligus.</summary>
internal static class Log
{
    private static readonly Lock Gate = new();

    public static void Line(string text)
    {
        lock (Gate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
    }
}
