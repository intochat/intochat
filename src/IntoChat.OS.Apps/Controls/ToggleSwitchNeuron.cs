/*
# Toggle switch

A control neuron: an on/off switch. It owns whether it is on and signals Toggled when the value changes. Its label, on and off
texts, enabled and accessible name are props its parent writes on the slot; the parent refuses commands while disabled.
It replaces the legacy Switch data node (whose emit went to the owning app) for new code.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A toggle switch: it owns whether it is on and signals <see cref="Toggled"/>.</summary>
[Neuron("control.toggleSwitch"), Emits(typeof(Toggled))]
public sealed class ToggleSwitchNeuron : ControlNeuron<ToggleSwitchState, ToggleSwitchProps>
{
    [On]
    public Task Toggle(Toggle command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Set(!(Props?.IsOn ?? State.IsOn));
    }

    [On]
    public Task SetChecked(SetChecked command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Set(command.IsOn);
    }

    private Task Set(bool isOn) => SetOn(Props?.IsOn, State.IsOn, isOn, on => State with { IsOn = on });

    protected override View Render(ToggleSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new ToggleSwitchView(Key, state.IsOn));
    }
}
