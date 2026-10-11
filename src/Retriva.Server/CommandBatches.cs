using Retriva.Shared.Protocol;

namespace Retriva.Server;

public sealed record BatchSummary(
    string Action, int Expected, int Succeeded, int Failed, int NoReply,
    IReadOnlyList<string> FailedUnits, IReadOnlyList<string> SilentUnits)
{
    public bool AllGood => Failed == 0 && NoReply == 0;

    public string Describe() => AllGood
        ? $"{Succeeded} unit menjawab berhasil"
        : $"{Succeeded} berhasil · {Failed} gagal · {NoReply} tidak menjawab";
}

/// <summary>
/// Melacak perintah massal sampai seluruh unit menjawab atau waktu habis.
///
/// Perintah yang disebar ke 40 unit memakai satu CommandId yang sama, sehingga
/// balasan dapat dikelompokkan berdasarkan id itu. Tanpa pelacakan ini, admin
/// hanya tahu perintah sudah dikirim — bukan berapa yang benar-benar menurut,
/// dan pada 40 unit perbedaan itu yang justru penting.
/// </summary>
public sealed class BatchTracker
{
    private sealed class Batch
    {
        public required string Action { get; init; }
        public required HashSet<string> Pending { get; init; }
        public required int Expected { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public List<string> Failed { get; } = [];
        public int Succeeded { get; set; }
    }

    private readonly Dictionary<Guid, Batch> _batches = [];
    private readonly Lock _gate = new();

    public event Action<BatchSummary>? Completed;

    public void Begin(Guid commandId, string action, IEnumerable<string> expectedUnits, TimeSpan timeout)
    {
        var units = new HashSet<string>(expectedUnits, StringComparer.OrdinalIgnoreCase);
        if (units.Count == 0) return;

        lock (_gate)
        {
            _batches[commandId] = new Batch
            {
                Action = action,
                Pending = units,
                Expected = units.Count,
                Deadline = DateTimeOffset.UtcNow + timeout,
            };
        }
    }

    /// <summary>Mencatat balasan satu unit. Mengembalikan true bila ini bagian dari batch.</summary>
    public bool Record(CommandResult result)
    {
        BatchSummary? summary = null;

        lock (_gate)
        {
            if (!_batches.TryGetValue(result.CommandId, out var batch)) return false;
            if (!batch.Pending.Remove(result.UnitId)) return true;

            if (result.Outcome == CommandOutcome.Success) batch.Succeeded++;
            else batch.Failed.Add($"{result.UnitId} ({result.Detail})");

            if (batch.Pending.Count == 0)
            {
                summary = Close(result.CommandId, batch);
            }
        }

        if (summary is not null) Completed?.Invoke(summary);
        return true;
    }

    /// <summary>
    /// Menutup batch yang lewat tenggat. Unit yang mati mendadak tidak pernah
    /// menjawab, jadi tanpa tenggat batch akan menggantung selamanya.
    /// </summary>
    public void SweepExpired()
    {
        List<BatchSummary> finished = [];

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (id, batch) in _batches.Where(b => b.Value.Deadline <= now).ToList())
                finished.Add(Close(id, batch));
        }

        foreach (var summary in finished) Completed?.Invoke(summary);
    }

    private BatchSummary Close(Guid id, Batch batch)
    {
        _batches.Remove(id);
        return new BatchSummary(
            batch.Action, batch.Expected, batch.Succeeded, batch.Failed.Count,
            batch.Pending.Count, batch.Failed, [.. batch.Pending]);
    }
}
