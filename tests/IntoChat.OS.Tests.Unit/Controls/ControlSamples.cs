using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using SelectCommand = DigitalBrain.UI.Commands.Select;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

// Each control neuron's sample: the props a parent gives it, a starting state, and commands that, run in order, exercise every command it takes.
// A new control neuron adds its row here; ControlEmitsFacts fails for a control without one.
internal static class ControlSamples
{
    internal sealed record Sample(ControlProps Props, object State, Signal[] Commands);

    internal static readonly IReadOnlyDictionary<Type, Sample> All = new Dictionary<Type, Sample>
    {
        [typeof(CheckBoxNeuron)] = new(new CheckBoxProps("Terms"), new CheckBoxState(), [new Toggle(), new SetChecked(false), new SetChecked(true)]),
        [typeof(ToggleSwitchNeuron)] = new(new ToggleSwitchProps("Dark mode"), new ToggleSwitchState(), [new Toggle(), new SetChecked(false), new SetChecked(true)]),
        [typeof(ExpanderNeuron)] = new(new ExpanderProps("Advanced", [new Text("Inside")]), new ExpanderState(), [new Expand(true), new Expand(false)]),
        [typeof(TabStripNeuron)] = new(new TabStripProps([new TabStripItem("a", "A"), new TabStripItem("b", "B")]), new TabStripState(), [new SelectCommand("a"), new SelectCommand("b")]),
        [typeof(ComboBoxNeuron)] = new(new ComboBoxProps("Theme", [new ComboBoxOption("light", "Light"), new ComboBoxOption("dark", "Dark")]), new ComboBoxState(), [new SelectCommand("light"), new SelectCommand("dark")]),
    };
}
