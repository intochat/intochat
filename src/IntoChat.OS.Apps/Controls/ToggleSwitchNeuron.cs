/*
# Toggle switch

A control neuron: an on/off switch with a header and optional on and off texts. It owns whether it is on and signals Toggled when
the value changes. It replaces the legacy Switch data node (whose emit went to the owning app) for new code.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A toggle switch: it owns whether it is on and signals <see cref="Toggled"/>.</summary>
[Neuron("control.toggleSwitch"), Emits(typeof(Toggled))]
public sealed class ToggleSwitchNeuron : Screen<ToggleSwitchState>
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

    private Task Set(bool isOn) => isOn == State.IsOn ? Task.CompletedTask : Show(State with { IsOn = isOn }, new Toggled(isOn));

    protected override View Render(ToggleSwitchState state)
    {
        var key = ControlKeys.Of(Id);
        return new(state.Label, new ToggleSwitchView(key, state.Label, state.IsOn, state.AutomationName ?? key, state.OnContent, state.OffContent));
    }
}
