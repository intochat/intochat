using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Parts;

// A signal published without a state write is its own entry in the publisher's part log.
internal sealed class PartSignalJournal(PartLogs logs) : ISignalJournal
{
    public async Task Append(GrainId publisher, Signal[] signals)
    {
        var log = await logs.For(PartId.Of(publisher.Key.ToString()!));
        await log.Append(publisher.ToString(), "", RecordChange.None, [], signals);
    }

    // Parts number their entries separately, so the brain's order is by entry seq, then part: stable while the
    // set of open parts is, and enough for a replay that reads from the start.
    public async Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit)
    {
        List<(long Seq, string Part, Signal Signal)> all = [];
        foreach (var log in await logs.OpenFor(brainId))
        {
            foreach (var entry in log.SignalsSince(0)) { all.AddRange(entry.Signals.Select(s => (entry.Seq, log.Part.Value, s))); }
        }
        return [.. all.OrderBy(e => e.Seq).ThenBy(e => e.Part, StringComparer.Ordinal)
            .Select((e, i) => new JournaledSignal(i + 1, e.Signal))
            .Where(j => j.Sequence > since).Take(limit)];
    }
}
