/*
# Check box

A control neuron: a box that a person checks or clears. It owns whether it is checked and signals Toggled when the value changes.
Its label, enabled and accessible name are props its parent writes on the slot; the parent refuses commands while disabled.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A check box: it owns whether it is checked and signals <see cref="Toggled"/>.</summary>
[Neuron("control.checkBox"), Emits(typeof(Toggled))]
public sealed class CheckBoxNeuron : ControlNeuron<CheckBoxState, CheckBoxProps>
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
        return isOn == State.IsOn ? Task.CompletedTask : Show(State with { IsOn = isOn }, new Toggled(isOn));
    }

    protected override View Render(CheckBoxState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new CheckBoxView(Key, state.IsOn));
    }
}
