using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Rating.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Rating;

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
