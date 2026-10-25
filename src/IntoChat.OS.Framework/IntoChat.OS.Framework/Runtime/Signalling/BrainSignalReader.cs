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
