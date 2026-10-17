namespace DigitalBrain.Framework;

// Where a neuron's signals become durable before anyone sees them (Q27). Hosts without a part log journal
// nothing; on a machine, signals ride in the same log entry as the state write that caused them, or alone.
public interface ISignalJournal
{
    // A signal published without a state write: durable once this completes.
    Task Append(GrainId publisher, Signal[] signals);

    // The brain's journalled signals after `since` (0 reads from the start), in sequence order, at most `limit`.
    Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit);

    // One publisher's signals in entries after part-log seq `since`, each with its entry's seq; null on a host that
    // journals nothing. Throws ServiceUnavailableException when the publisher's part is leased by another machine.
    Task<PartSignals?> SignalsOf(GrainId publisher, long since);
}

// Last is the part log's last seq, where a new reader starts. Entries at or below Floor may no longer be served.
public sealed record PartSignals(long Floor, long Last, JournaledSignal[] Signals);

// Sequence numbers are the brain's own ordering of its journalled signals: strictly increasing, 1-based.
public sealed record JournaledSignal(long Sequence, Signal Signal);

public static class SignalJournal
{
    private sealed record Batch(GrainId Publisher, List<Signal> Signals);

    private static readonly AsyncLocal<Batch?> Pending = new();

    // Signals a Save is about to publish. Only the storage write for this same grain takes them, so a write
    // by another grain nested inside the storage call never carries them.
    public static IDisposable Attach(GrainId publisher, Signal[] signals)
    {
        var previous = Pending.Value;
        Pending.Value = new Batch(publisher, [.. signals]);
        return new Scope(previous);
    }

    public static Signal[] TakePending(GrainId grainId)
    {
        if (Pending.Value is not { } pending || pending.Publisher != grainId) { return []; }
        Signal[] taken = [.. pending.Signals];
        pending.Signals.Clear();
        return taken;
    }

    // A storage write that was refused before anything committed hands its signals back to the Save behind it.
    public static void Restore(GrainId grainId, Signal[] signals)
    {
        if (signals.Length == 0 || Pending.Value is not { } pending || pending.Publisher != grainId) { return; }
        pending.Signals.InsertRange(0, signals);
    }

    internal static bool Taken => Pending.Value is { Signals.Count: 0 };

    private sealed class Scope(Batch? previous) : IDisposable
    {
        public void Dispose() => Pending.Value = previous;
    }
}

internal sealed class NoSignalJournal : ISignalJournal
{
    public Task Append(GrainId publisher, Signal[] signals) => Task.CompletedTask;

    public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => Task.FromResult<JournaledSignal[]>([]);

    public Task<PartSignals?> SignalsOf(GrainId publisher, long since) => Task.FromResult<PartSignals?>(null);
}
