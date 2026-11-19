using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Rating.Signals;

namespace IntoChat.OS.Modules.UI.Rating;

[Neuron("ui.rating")]
internal sealed class RatingNeuron : Neuron<RatingState>, IRating
{
    public async Task<RatingState> Configure(string label, int value = 0, int max = 5, bool isEnabled = true)
    {
        var resolvedMax = Math.Clamp(max, 1, 10);
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            Max = resolvedMax,
            Value = Math.Clamp(value, 0, resolvedMax),
            IsEnabled = isEnabled,
        };
        await Save(next, new RatingChanged(Id.FullKey(), next.Version, next.Value));
        return next;
    }

    public async Task<RatingState> SetValue(int value)
    {
        if (!State.IsEnabled) { throw new InvalidOperationException($"Rating '{Id.FullKey()}' is disabled."); }
        var clamped = Math.Clamp(value, 0, State.Max);
        var next = State with { Version = State.Version + 1, Value = clamped };
        await Save(next, new RatingChanged(Id.FullKey(), next.Version, next.Value));
        return next;
    }

    public Task<RatingState> Read() => Task.FromResult(State);
}
