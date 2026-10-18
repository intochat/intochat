using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Toggle.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Toggle;

[GrainType(UIVocabulary.ToggleType)]
internal sealed class ToggleNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ToggleState> store)
    : Neuron<ToggleState>(store), IToggle
{
    public Task Set(string label, bool on)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Label = label.Trim();
        next.On = on;
        return Save(next, new ToggleChanged(this.GetPrimaryKeyString(), next.On));
    }

    public Task Flip()
    {
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.On = !next.On;
        return Save(next, new ToggleChanged(this.GetPrimaryKeyString(), next.On));
    }

    [ReadOnly] public Task<ToggleState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
