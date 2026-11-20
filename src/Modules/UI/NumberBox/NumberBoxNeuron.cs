using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.NumberBox.Signals;

namespace IntoChat.OS.Modules.UI.NumberBox;

[Neuron("ui.number-box")]
internal sealed class NumberBoxNeuron : UiNeuronBase<NumberBoxState>, INumberBox
{
    public async Task<NumberBoxState> Configure(string label, double value = 0, double min = 0, double max = 1000, double step = 1, bool isEnabled = true, string prefix = "")
    {
        var clamped = Math.Clamp(value, min, max);
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            Value = clamped,
            Min = min,
            Max = max,
            Step = step <= 0 ? 1 : step,
            IsEnabled = isEnabled,
            Prefix = prefix ?? "",
        };
        await Save(next, new NumberBoxChanged(Id.FullKey(), next.Version, next.Value));
        return next;
    }

    public async Task<NumberBoxState> SetValue(double value)
    {
        if (!State.IsEnabled) { throw new InvalidOperationException($"NumberBox '{Id.FullKey()}' is disabled."); }
        var clamped = Math.Clamp(value, State.Min, State.Max);
        var next = State with { Version = State.Version + 1, Value = clamped };
        await Save(next, new NumberBoxChanged(Id.FullKey(), next.Version, next.Value));
        return next;
    }

    public Task<NumberBoxState> Increment() => SetValue(State.Value + State.Step);
    public Task<NumberBoxState> Decrement() => SetValue(State.Value - State.Step);
    public Task<NumberBoxState> Read() => Task.FromResult(State);
}
