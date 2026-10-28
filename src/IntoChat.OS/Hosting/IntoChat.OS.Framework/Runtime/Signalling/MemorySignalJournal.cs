using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

public sealed class MemorySignalJournalOptions
{
    // How many of a brain's newest signals the ring keeps; older ones fall below the floor.
    public int Retain { get; set; } = 4096;
}

// The journal of a host without a part log: one bounded ring per brain, sequenced per brain from 1. Nothing
// survives the host; signals at or below the floor are gone, so a reader whose `since` is below it has missed some.
public sealed class MemorySignalJournal(IOptions<MemorySignalJournalOptions> options) : ISignalJournal
{
    private readonly int retain = options.Value.Retain > 0
        ? options.Value.Retain
        : throw new ArgumentOutOfRangeException(nameof(options), "Retain must be positive.");

    private readonly ConcurrentDictionary<string, Ring> rings = new(StringComparer.Ordinal);

    public Task<long> Append(GrainId publisher, Signal[] signals)
    {
        var ring = RingOf(BrainOf(publisher));
        lock (ring)
        {
            foreach (var signal in signals)
            {
                ring.Entries.Enqueue(new Entry(publisher, new JournaledSignal(++ring.Last, signal)));
                if (ring.Entries.Count > retain) { ring.Entries.Dequeue(); }
            }
            return Task.FromResult(ring.Last);
        }
    }

    public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit)
    {
        var ring = RingOf(brainId);
        lock (ring)
        {
            return Task.FromResult(ring.Entries.Where(entry => entry.Signal.Sequence > since).Take(limit).Select(entry => entry.Signal).ToArray());
        }
    }

    public Task<PartSignals?> SignalsOf(GrainId publisher, long since)
    {
        var ring = RingOf(BrainOf(publisher));
        lock (ring)
        {
            var floor = ring.Entries.TryPeek(out var oldest) ? oldest.Signal.Sequence - 1 : ring.Last;
            var signals = ring.Entries.Where(entry => entry.Publisher == publisher && entry.Signal.Sequence > since).Select(entry => entry.Signal).ToArray();
            return Task.FromResult<PartSignals?>(new PartSignals(floor, ring.Last, signals));
        }
    }

    private Ring RingOf(string brainId) => rings.GetOrAdd(brainId, static _ => new Ring());

    private static string BrainOf(GrainId publisher) => BrainKey.BrainOf(publisher.Key.ToString()!);

    private sealed record Entry(GrainId Publisher, JournaledSignal Signal);

    private sealed class Ring
    {
        public Queue<Entry> Entries { get; } = new();

        public long Last { get; set; }
    }
}
