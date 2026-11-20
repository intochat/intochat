using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Toggle.Signals;

namespace IntoChat.OS.Modules.UI.Toggle;

[Neuron("ui.toggle")]
internal sealed class ToggleNeuron : UiNeuronBase<ToggleState>, IToggle
{
    public async Task<ToggleState> Configure(string label, bool? isOn = null, bool isEnabled = true, string onLabel = "On", string offLabel = "Off")
    {
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            IsOn = isOn ?? State.IsOn,
            IsEnabled = isEnabled,
            OnLabel = string.IsNullOrWhiteSpace(onLabel) ? "On" : onLabel,
            OffLabel = string.IsNullOrWhiteSpace(offLabel) ? "Off" : offLabel,
        };
        await Save(next, new ToggleChanged(Id.FullKey(), next.Version, next.IsOn));
        return next;
    }

    public Task<ToggleState> Toggle() => Set(!State.IsOn);

    public async Task<ToggleState> Set(bool isOn)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"Toggle '{Id.FullKey()}' is disabled.");
        }
        if (State.IsOn == isOn) { return State; }
        var next = State with { Version = State.Version + 1, IsOn = isOn };
        await Save(next, new ToggleChanged(Id.FullKey(), next.Version, next.IsOn));
        return next;
    }

    public async Task<ToggleState> SetEnabled(bool isEnabled)
    {
        if (State.IsEnabled == isEnabled) { return State; }
        var next = State with { Version = State.Version + 1, IsEnabled = isEnabled };
        await Save(next, new ToggleChanged(Id.FullKey(), next.Version, next.IsOn));
        return next;
    }

    public Task<ToggleState> Read() => Task.FromResult(State);
}
