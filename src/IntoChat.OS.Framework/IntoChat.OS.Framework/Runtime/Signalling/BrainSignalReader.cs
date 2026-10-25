using Orleans.Concurrency;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

// Serves a publisher's signal history from the silo journal, so reading it never activates the publisher.
// Keyed by the brain id, as BrainKey.BrainOf derives it, and placed with that brain so it reads the owning silo's journal.
[Alias("brain.signal-reader"), Orleans.Metadata.DefaultGrainType("brain.signal-reader")]
public interface IBrainSignalReader : IGrainWithStringKey
{
    [AlwaysInterleave, ReadOnly] Task<SignalPage> Read(NeuronId publisher, long since, int limit);
}

[GrainType("brain.signal-reader"), BrainAffinePlacement, Reentrant]
internal sealed class BrainSignalReaderGrain(ISignalJournal journal) : Grain, IBrainSignalReader
{
    public Task<SignalPage> Read(NeuronId publisher, long since, int limit)
    {
        var id = GrainId.Parse(publisher.Name);
        if (BrainKey.BrainOf(id.Key.ToString()!) != this.GetPrimaryKeyString())
        { throw new UnauthorizedAccessException("A brain's signal reader serves only that brain's publishers."); }
        return SignalPages.Read(journal, id, since, limit);
    }
}
