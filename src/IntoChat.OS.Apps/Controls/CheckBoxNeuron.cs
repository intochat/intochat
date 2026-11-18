/*
# Check box

A control neuron: a box with a label that a person checks or clears. It owns whether it is checked, refuses commands while disabled
(control.disabled) and signals Toggled when the value changes.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A check box: it owns whether it is checked, refuses commands while disabled and signals <see cref="Toggled"/>.</summary>
[Neuron("control.checkBox"), Emits(typeof(Toggled))]
public sealed class CheckBoxNeuron : Screen<CheckBoxState>
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
        if (!State.IsEnabled) { throw new ActivationRefusedException(ControlRefusals.Disabled); }
        return isOn == State.IsOn ? Task.CompletedTask : Show(State with { IsOn = isOn }, new Toggled(isOn));
    }

    protected override View Render(CheckBoxState state)
    {
        var key = ControlKeys.Of(Id);
        return new(state.Label, new CheckBoxView(key, state.Label, state.IsOn, state.IsEnabled, state.AutomationName ?? key, state.IsThreeState));
    }
}
