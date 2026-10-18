using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Progress.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Progress;

[GrainType(UIVocabulary.ProgressType)]
internal sealed class ProgressNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ProgressState> store)
    : Neuron<ProgressState>(store), IProgress
{
    public Task Set(bool determinate, double value, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Determinate = determinate;
        next.Value = Math.Clamp(value, 0, 1);
        next.Label = label;
        return Save(next, new ProgressChanged(this.GetPrimaryKeyString(), next.Value));
    }

    [ReadOnly] public Task<ProgressState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
