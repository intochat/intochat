using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using SelectCommand = DigitalBrain.UI.Commands.Select;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Modules.UI.Controls;

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
        [typeof(FormNeuron)] = new(new FormProps([new FormInput("name", "Name"), new FormInput("bio", "Bio", FormFieldKind.Multiline)]), new FormState(),
            [new Submit(new Dictionary<string, string> { ["name"] = "Ada", ["bio"] = "Engineer" })]),
        [typeof(ListBoxNeuron)] = new(new ListBoxProps([new ListBoxItem("a", "A"), new ListBoxItem("b", "B", "Second")]), new ListBoxState(), [new SelectCommand("a"), new SelectCommand("b")]),
        [typeof(LiveChartNeuron)] = new(new LiveChartProps("Spend"), new LiveChartState(), [new Replace([new LivePoint("a", "Mon", 1)]), new Append(new LivePoint("b", "Tue", 2))]),
        [typeof(QueryTableNeuron)] = new(new QueryTableProps("People", [new QueryTableColumn("name", "Name")], Rows: [new QueryTableRow("ada", ["Ada"])]), new QueryTableState(),
            [new Filter("a"), new Sort("name"), new Page(2), new SelectRow("ada")]),
        [typeof(KanbanNeuron)] = new(new KanbanProps("Work", [new KanbanColumnItem("todo", "To do"), new KanbanColumnItem("done", "Done")], [new KanbanCardItem("a", "todo", "Write")]),
            new KanbanState(), [new SelectCommand("a"), new MoveCard("a", "done", 0)]),
        [typeof(StepperNeuron)] = new(new StepperProps([new StepperItem("plan", "Plan"), new StepperItem("ship", "Ship")]), new StepperControlState(), [new SelectCommand("plan"), new SelectCommand("ship")]),
        [typeof(TreeNeuron)] = new(new TreeProps([new TreeItem("code", "code", IsFolder: true), new TreeItem("app", "app.cs", "code")]), new TreeState(),
            [new SelectCommand("app"), new ExpandNode("code", true)]),
        [typeof(SliderNeuron)] = new(new SliderProps("Depth", 0, 10), new SliderControlState(), [new SetValue(3), new SetValue(4)]),
        [typeof(ChatThreadNeuron)] = new(new ChatThreadProps("Ask anything."), new ChatThreadState(),
            [new AppendMessage(new ThreadMessage("m1", "assistant", "", "failed")), new UpdateMessage("m1", new ThreadMessagePatch(Failure: "timed-out")),
             new ImportMessages([new ThreadMessage("m2", "user", "hi")]), new Retry("m1"), new SubmitText("hello"), new Stop(), new SelectModel("gpt-5.4")]),
    };
}
