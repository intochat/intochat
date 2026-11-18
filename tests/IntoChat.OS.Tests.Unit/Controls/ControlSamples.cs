using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

// Each control neuron's sample: a starting state and commands that, run in order, exercise every command it takes.
// A new control neuron adds its row here; ControlEmitsFacts fails for a control without one.
internal static class ControlSamples
{
    internal sealed record Sample(object State, Signal[] Commands);

    internal static readonly IReadOnlyDictionary<Type, Sample> All = new Dictionary<Type, Sample>
    {
        [typeof(CheckBoxNeuron)] = new(new CheckBoxState { Label = "Terms" }, [new Toggle(), new SetChecked(false), new SetChecked(true)]),
        [typeof(ToggleSwitchNeuron)] = new(new ToggleSwitchState { Label = "Dark mode" }, [new Toggle(), new SetChecked(false), new SetChecked(true)]),
        [typeof(ExpanderNeuron)] = new(new ExpanderState { Header = "Advanced", Children = [new Text("Inside")] }, [new Expand(true), new Expand(false)]),
    };
}
