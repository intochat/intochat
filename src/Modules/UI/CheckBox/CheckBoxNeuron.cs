using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.CheckBox.Signals;

namespace IntoChat.OS.Modules.UI.CheckBox;

[Neuron("ui.checkbox")]
internal sealed class CheckBoxNeuron : Neuron<UiCheckBoxState>, ICheckBox
{
    public async Task<UiCheckBoxState> Configure(string label, bool? isChecked = null, bool isEnabled = true)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            IsChecked = isChecked ?? State.IsChecked,
            IsEnabled = isEnabled,
        };
        await Save(next, new CheckBoxChanged(Id.FullKey(), next.Version, next.IsChecked));
        return next;
    }

    public Task<UiCheckBoxState> Toggle() => Set(!State.IsChecked);

    public async Task<UiCheckBoxState> Set(bool isChecked)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"CheckBox '{Id.FullKey()}' is disabled.");
        }
        if (State.IsChecked == isChecked) { return State; }
        var next = State with { Version = State.Version + 1, IsChecked = isChecked };
        await Save(next, new CheckBoxChanged(Id.FullKey(), next.Version, next.IsChecked));
        return next;
    }

    public Task<UiCheckBoxState> Read() => Task.FromResult(State);
}
