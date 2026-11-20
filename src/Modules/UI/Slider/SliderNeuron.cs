using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Slider.Signals;

namespace IntoChat.OS.Modules.UI.Slider;

[Neuron("ui.slider")]
internal sealed class SliderNeuron : UiNeuronBase<SliderState>, ISlider
{
    public async Task<SliderState> Configure(string label, double min = 0, double max = 100, double step = 1, double? value = null, bool isEnabled = true, string unit = "")
    {
        if (max <= min) { throw new ArgumentException("Max must be greater than min.", nameof(max)); }
        var resolvedStep = step <= 0 ? 1 : step;
        var resolvedValue = Math.Clamp(value ?? State.Value, min, max);
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            Min = min,
            Max = max,
            Step = resolvedStep,
            Value = resolvedValue,
            IsEnabled = isEnabled,
            Unit = unit ?? "",
        };
        await Save(next, new SliderChanged(Id.FullKey(), next.Version, next.Value));
        return next;
    }

    public async Task<SliderState> SetValue(double value)
    {
        if (!State.IsEnabled) { throw new InvalidOperationException($"Slider '{Id.FullKey()}' is disabled."); }
        var clamped = Math.Clamp(value, State.Min, State.Max);
        var next = State with { Version = State.Version + 1, Value = clamped };
        await Save(next, new SliderChanged(Id.FullKey(), next.Version, next.Value));
        return next;
    }

    public Task<SliderState> Read() => Task.FromResult(State);
}
