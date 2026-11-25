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

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A toggle switch: it owns whether it is on and signals <see cref="Toggled"/>.</summary>
[Neuron("control.toggleSwitch"), Publishes(typeof(Toggled))]
public sealed class ToggleSwitchNeuron : ControlNeuron<ToggleSwitchState, ToggleSwitchProps>, IToggleSwitchControl
{
    [On]
    public Task SetChecked(SetChecked command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetOn(Props?.IsOn, State.IsOn, command.IsOn, Store);
    }

    private ToggleSwitchState Store(bool on) => State with { IsOn = on };

    protected override View Render(ToggleSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new ToggleSwitchView(Key, state.IsOn));
    }
}
