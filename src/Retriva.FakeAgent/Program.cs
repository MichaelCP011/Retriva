using Microsoft.AspNetCore.SignalR.Client;
using Retriva.FakeAgent;
using Retriva.Shared;
using Retriva.Shared.Config;
using Retriva.Shared.Protocol;

// Simulator unit mahasiswa untuk menguji perilaku skala penuh di satu mesin.
//
//   dotnet run --project src/Retriva.FakeAgent -- --count 40
//   dotnet run --project src/Retriva.FakeAgent -- --unit PC-07
//   dotnet run --project src/Retriva.FakeAgent -- --count 40 --chaos
//   dotnet run --project src/Retriva.FakeAgent -- --count 40 --prefix SIMPEL-
//
// Satu proses menangani semua unit. Menjalankan 40 proses terpisah akan
// memakan sekitar 2 GB RAM tanpa menguji apa pun yang berbeda.

// Awalan nama unit. Bawaannya PC- supaya perintah lama tetap berperilaku sama.
var prefix = Arg("--prefix") ?? "PC-";

var host   = Arg("--console") ?? "127.0.0.1";
var port   = int.TryParse(Arg("--port"), out var p) ? p : RetrivaProtocol.DefaultConsolePort;
var chaos  = HasFlag("--chaos");

int from = 1, to = 1;
if (Arg("--unit") is { } single && int.TryParse(single.AsSpan(single.LastIndexOf('-') + 1), out var n))
{
    from = to = n;
}
else if (int.TryParse(Arg("--count"), out var count))
{
    from = 1; to = count;
}
else
{
    from = int.TryParse(Arg("--from"), out var f) ? f : 1;
    to   = int.TryParse(Arg("--to"),   out var t) ? t : from;
}

// Berjalan di mesin yang sama dengan konsol, jadi kepercayaan diambil dari
// berkas lokal yang ditulis konsol saat start.
var pin = Arg("--pin") ?? ReadFileOrNull(RetrivaPaths.ThumbprintFile);
var secret = Arg("--secret")
             ?? ConfigStore.LoadOrCreate<ConsoleConfig>(RetrivaPaths.ConsoleConfigFile)
                           .AgentEnrollmentSecret;

if (string.IsNullOrWhiteSpace(pin))
{
    Console.WriteLine("Sidik jari sertifikat tidak ditemukan. Jalankan Retriva.Admin lebih dulu, "
                    + "atau berikan --pin <thumbprint>.");
    return 1;
}

var options = new SimulationOptions
{
    Host = host, Port = port, Pin = pin, Secret = secret ?? "",
    From = from, To = to, Chaos = chaos,
};

var stats = new Stats();
var units = Enumerable.Range(from, to - from + 1)
    // Awalan dapat diganti: --prefix SIMPEL- menghasilkan SIMPEL-01..SIMPEL-40.
    //
    // Berguna untuk menguji lab yang penamaannya bukan PC-xx, dan untuk
    // memastikan panel tidak mengandaikan pola nama tertentu di mana pun.
    .Select(i => new SimulatedUnit($"{prefix}{i:D2}", options, stats))
    .ToList();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Log.Line($"Menyalakan {units.Count} unit tiruan menuju https://{host}:{port}"
       + (chaos ? " · mode chaos aktif" : ""));

var running = new List<Task>();
foreach (var unit in units)
{
    running.Add(RunUnitAsync(unit, cts.Token));
    // Distart bertahap: menyalakan 40 koneksi serentak adalah lonjakan
    // buatan yang tidak pernah terjadi di lab sungguhan.
    await Task.Delay(50, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
}

running.Add(ReportLoopAsync(units, stats, cts.Token));
if (chaos) running.Add(ChaosLoopAsync(units, cts.Token));

try { await Task.WhenAll(running); }
catch (OperationCanceledException) { }

Log.Line("Simulator berhenti.");
return 0;

static async Task RunUnitAsync(SimulatedUnit unit, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await unit.RunAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Line($"[{unit.UnitId}] {ex.GetType().Name}: {ex.Message}");
        }

        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }
    }
}

static async Task ReportLoopAsync(List<SimulatedUnit> units, Stats stats, CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    while (await timer.WaitForNextTickAsync(ct))
    {
        var connected = units.Count(u => u.State == HubConnectionState.Connected);
        Log.Line(stats.Summary(connected, units.Count));
    }
}

/// <summary>
/// Memutus unit acak secara berkala. Ini yang menguji watchdog di konsol dan
/// pendaftaran ulang di agent — dua hal yang tidak akan pernah terlihat salah
/// selama semuanya berjalan mulus.
/// </summary>
static async Task ChaosLoopAsync(List<SimulatedUnit> units, CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
    while (await timer.WaitForNextTickAsync(ct))
    {
        var victim = units[Random.Shared.Next(units.Count)];
        Log.Line($"[chaos] memutus {victim.UnitId}");
        _ = victim.BounceAsync(ct);
    }
}

static string? Arg(string name)
{
    var argv = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static bool HasFlag(string name) =>
    Environment.GetCommandLineArgs().Contains(name);

static string? ReadFileOrNull(string path)
{
    try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
    catch (IOException) { return null; }
}
