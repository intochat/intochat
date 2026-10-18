using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Clock.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Clock;

[GrainType(UIVocabulary.ClockType)]
internal sealed class ClockNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ClockState> store)
    : Neuron<ClockState>(store), IClock
{
    public Task Set(string label, DateTimeOffset? dueAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Label = label.Trim();
        next.DueAt = dueAt;
        return Save(next, new ClockChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly] public Task<ClockState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
