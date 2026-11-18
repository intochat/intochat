/*
# Check box

A control neuron: a box with a label that a person checks or clears. It owns whether it is checked, refuses commands while disabled
(ControlDisabled, control.disabled) and signals Toggled when the value changes.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A check box: it owns whether it is checked, refuses commands while disabled and signals <see cref="Toggled"/>.</summary>
[Neuron("control.checkBox"), Emits(typeof(Toggled))]
public sealed class CheckBoxNeuron : ControlNeuron<CheckBoxState>
{
    [On]
    public Task Toggle(Toggle command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Set(!State.IsOn);
    }

    [On]
    public Task SetChecked(SetChecked command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Set(command.IsOn);
    }

    private Task Set(bool isOn)
    {
        EnsureEnabled();
        return isOn == State.IsOn ? Task.CompletedTask : Show(State with { IsOn = isOn }, new Toggled(isOn));
    }

    protected override View Render(CheckBoxState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(state.Label, new CheckBoxView(Key, state.Label, state.IsOn, state.IsEnabled, state.AutomationName ?? Key));
    }
}
