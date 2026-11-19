using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Progress.Signals;

namespace IntoChat.OS.Modules.UI.Progress;

[Neuron("ui.progress")]
internal sealed class ProgressIndicatorNeuron : Neuron<ProgressState>, IProgressIndicator
{
    public async Task<ProgressState> SetProgress(string label, double value, bool isIndeterminate = false, string kind = "bar")
    {
        var clamped = Math.Clamp(value, 0d, 1d);
        var normalizedKind = string.Equals(kind, "ring", StringComparison.OrdinalIgnoreCase) ? "ring" : "bar";
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            Value = clamped,
            IsIndeterminate = isIndeterminate,
            Kind = normalizedKind,
        };
        await Save(next, new ProgressChanged(Id.FullKey(), next.Version, next.Value, next.IsIndeterminate));
        return next;
    }

    public Task<ProgressState> Read() => Task.FromResult(State);
}
