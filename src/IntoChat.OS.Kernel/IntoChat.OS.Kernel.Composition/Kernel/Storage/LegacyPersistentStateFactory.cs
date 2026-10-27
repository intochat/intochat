using IntoChat.OS.Contracts.Configuration;
using Orleans.Core;
using Orleans.Metadata;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-02, R4-12, deleted in 06-08): the runtime BrainLog is the cluster store, and the runtime's host grains
// require exactly that store. An unmigrated Framework grain that declares [PersistentState(name, "cluster")] keeps its
// declaration (state names and stores never move, NeuronGrainTypes.json) and is handed the legacy store instead, which writes
// into the same BrainLog through IBrainJournalWriter. A grain class from a DigitalBrain assembly keeps the cluster store.
internal sealed class LegacyPersistentStateFactory(IPersistentStateFactory inner, GrainClassMap classes) : IPersistentStateFactory
{
    public IPersistentState<TState> Create<TState>(IGrainContext context, IPersistentStateConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cfg);
        if (!string.Equals(cfg.StorageName, IntoChatOSNames.ClusterGrainStorage, StringComparison.Ordinal) || IsRuntimeGrain(context.GrainId.Type))
        {
            return inner.Create<TState>(context, cfg);
        }
        return inner.Create<TState>(context, new Legacy(cfg.StateName));
    }

    private bool IsRuntimeGrain(GrainType type)
        => classes.TryGetGrainClass(type, out var grainClass)
            && grainClass.Assembly.GetName().Name is { } name && name.StartsWith("DigitalBrain.", StringComparison.Ordinal);

    private sealed record Legacy(string StateName) : IPersistentStateConfiguration
    {
        public string StorageName => IntoChatOSNames.LegacyClusterGrainStorage;
    }
}
