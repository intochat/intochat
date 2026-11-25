/*
# Check box

A control neuron: a box that a person checks or clears. It owns whether it is checked and signals Toggled when the value changes.
Its label, enabled and accessible name are props its parent writes on the child node; the parent refuses commands while disabled.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A check box: it owns whether it is checked and signals <see cref="Toggled"/>.</summary>
[Neuron("control.checkBox"), Publishes(typeof(Toggled))]
public sealed class CheckBoxNeuron : ControlNeuron<CheckBoxState, CheckBoxProps>, ICheckBoxControl
{
    [On]
    public Task SetChecked(SetChecked command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetOn(Props?.IsOn, State.IsOn, command.IsOn, Store);
    }

    private CheckBoxState Store(bool on) => State with { IsOn = on };

    protected override View Render(CheckBoxState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new CheckBoxView(Key, state.IsOn));
    }
}
