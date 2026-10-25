using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Rating.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Rating;

[GrainType(UIVocabulary.RatingType)]
internal sealed class RatingNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<RatingState> store)
    : Neuron<RatingState>(store), IRating
{
    public Task Set(int max, int value)
    {
        if (max < 1) { throw new ArgumentOutOfRangeException(nameof(max)); }
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Max = max;
        next.Value = Math.Clamp(value, 0, max);
        return Save(next, new RatingChanged(this.GetPrimaryKeyString(), next.Value));
    }

    [ReadOnly] public Task<RatingState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
