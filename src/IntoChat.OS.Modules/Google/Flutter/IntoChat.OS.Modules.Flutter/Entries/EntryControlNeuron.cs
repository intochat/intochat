using IntoChat.OS;
using IntoChat.OS.Flutter.Entries.Signals;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Entries;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Entries;

[GrainType(UIVocabulary.EntryControlType)]
internal sealed class EntryControlNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<EntryControlState> store)
    : Neuron<EntryControlState>(store), IEntryControl
{
    public Task Configure(EntryControlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Label);
        if (spec.Label.Length > 128) { throw new ArgumentException("An entry control label is at most 128 characters.", nameof(spec)); }
        switch (spec.Mode)
        {
            case EntryControlMode.Key when !BrainEntries.KeyedProviderIds.Any(id => string.Equals(spec.EntryName, BrainEntries.ProviderEntry(id), StringComparison.Ordinal)):
                throw new ArgumentException("A key control names provider:{id} for a key-based provider.", nameof(spec));
            case EntryControlMode.Collection when spec.EntryName is not null:
                throw new ArgumentException("A collection control names no entry.", nameof(spec));
            case EntryControlMode.Key or EntryControlMode.Collection:
                break;
            default:
                throw new ArgumentException("Unknown entry control mode.", nameof(spec));
        }
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Spec = spec with { Label = spec.Label.Trim() };
        return Save(next, new EntryControlChanged(next.Name));
    }

    [ReadOnly] public Task<EntryControlState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }

    public Task Changed() => PublishAsync(new EntryControlChanged(this.GetPrimaryKeyString()));
}
