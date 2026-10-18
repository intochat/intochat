namespace DigitalBrain.Framework;

// One page shape for every reader of a publisher's journalled signals: oldest first, at most `limit`,
// equal-length arrays; `limit <= 0` returns only Floor and Last.
public static class SignalPages
{
    public static SignalPage From(PartSignals? part, int limit)
    {
        if (part is null) { return new(0, 0, [], []); }
        var page = limit <= 0 ? [] : part.Signals.Take(limit).ToArray();
        return new(part.Floor, part.Last, [.. page.Select(entry => entry.Signal)], [.. page.Select(entry => entry.Sequence)]);
    }

    public static async Task<SignalPage> Read(ISignalJournal journal, GrainId publisher, long since, int limit)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return From(await journal.SignalsOf(publisher, since), limit);
    }
}
