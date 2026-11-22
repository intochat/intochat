using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record ControlledParentState
{
    public bool DarkMode { get; init; } = true;

    public string Theme { get; init; } = "light";
}

/*
# Controlled parent

A test parent that owns a switch's value and a combo box's selection (controlled slots), beside an uncontrolled switch.
*/
[Neuron("test.controlled-parent")]
public sealed class ControlledParent : Screen<ControlledParentState>
{
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        await FollowChild<ToggleSwitchNeuron, Toggled>("dark");
        await FollowChild<ComboBoxNeuron, SelectionChanged>("theme");
    }

    protected override View Render(ControlledParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<ToggleSwitchNeuron, ToggleSwitchProps>("dark", new ToggleSwitchProps("Dark mode", IsOn: state.DarkMode)),
            Child<ComboBoxNeuron, ComboBoxProps>("theme", new ComboBoxProps("Theme", [new ComboBoxOption("light", "Light"), new ComboBoxOption("dark", "Dark")], SelectedId: state.Theme)),
            Child<ToggleSwitchNeuron, ToggleSwitchProps>("free", new ToggleSwitchProps("Free")),
        ]));
    }

    [On]
    public Task OnToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        return SignalChild == "dark" ? Show(State with { DarkMode = toggled.IsOn }) : Task.CompletedTask;
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return SignalChild == "theme" ? Show(State with { Theme = changed.Id }) : Task.CompletedTask;
    }
}

[Signal("test.meddle")]
public sealed record Meddle(string Child) : Signal;

/*
# Meddler

A test neuron of the same brain that is not a control's parent: it sends a command with forged props straight to the control.
*/
[Neuron("test.meddler")]
public sealed class Meddler : Screen<ControlledParentState>
{
    protected override View Render(ControlledParentState state) => new("Meddler", new Text("meddler"));

    [On]
    public Task Meddle(Meddle meddle)
    {
        ArgumentNullException.ThrowIfNull(meddle);
        return Get<IScreen>(NeuronId.Parse(meddle.Child)).Dispatch(new Command(".", "control.toggle",
            Props: JsonSerializer.SerializeToElement(new ToggleSwitchProps("Forged", IsOn: true), JsonSerializerOptions.Web)));
    }
}

// A controlled control (the parent passes its value as a prop) only signals the person's change; the parent stores it and re-renders.
// Without a value prop the control keeps today's behaviour and stores the value itself.
public sealed class ControlledSlotFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.controlled-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public ControlledSlotFacts()
    {
        _os.Register<ControlledParent>();
        _os.Register<ToggleSwitchNeuron>();
        _os.Register<ComboBoxNeuron>();
        _os.Register<Meddler>();
    }

    private IScreen Parent => _os.Get<IScreen>(ParentId);

    private static NeuronId Child(string type, string key) => new(Brain, type, ScreenExtensions.ChildAddress(ParentId, key));

    private Signal[] Signals(NeuronId id) => [.. _os.Journal(Brain).Signals(id).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    private async Task<JsonElement> SlotProps(string key)
    {
        var view = await Parent.Read() ?? throw new InvalidOperationException("The parent has no view.");
        return view.View.GetProperty("root").GetProperty("children").EnumerateArray()
            .Single(node => node.GetProperty("neuronUri").GetString()!.EndsWith("/" + key, StringComparison.Ordinal)).GetProperty("props");
    }

    private async Task<T> Drawn<T>(NeuronId id) where T : Node
        => Assert.IsType<T>((await _os.Get<IScreen>(id).Read())!.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));

    private async Task Send(string key, Signal command)
    {
        var alias = command.GetType().GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias;
        await Parent.Dispatch(new Command(key, alias, JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));
        await _os.Idle();
    }

    [Fact]
    public async Task A_controlled_toggle_signals_the_new_value_stores_nothing_and_the_parent_rerenders_it()
    {
        await Parent.Read();

        await Send("dark", new Toggle());

        var dark = Child("control.toggleSwitch", "dark");
        Assert.Equal([new Toggled(false)], Signals(dark));
        Assert.False((await Drawn<ToggleSwitchView>(dark)).IsOn);
        Assert.False((await SlotProps("dark")).GetProperty("isOn").GetBoolean());

        await Send("dark", new Toggle());

        Assert.Equal([new Toggled(false), new Toggled(true)], Signals(dark));
        Assert.True((await SlotProps("dark")).GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task A_controlled_combo_box_signals_and_the_parent_owns_the_selection()
    {
        await Parent.Read();

        await Send("theme", new DigitalBrain.UI.Commands.Select("dark"));

        var theme = Child("control.comboBox", "theme");
        Assert.Equal([new SelectionChanged("dark")], Signals(theme));
        Assert.Null((await Drawn<ComboBoxView>(theme)).SelectedId);
        Assert.Equal("dark", (await SlotProps("theme")).GetProperty("selectedId").GetString());
    }

    [Fact]
    public async Task An_uncontrolled_toggle_stores_its_value()
    {
        await Parent.Read();

        await Send("free", new Toggle());

        var free = Child("control.toggleSwitch", "free");
        Assert.Equal([new Toggled(true)], Signals(free));
        Assert.True((await Drawn<ToggleSwitchView>(free)).IsOn);
    }

    [Fact]
    public async Task Props_from_a_caller_other_than_the_parent_are_dropped_and_logged()
    {
        var free = Child("control.toggleSwitch", "free");
        var warnings = new WarningListener();
        System.Diagnostics.Trace.Listeners.Add(warnings);
        try
        {
            await _os.Get<IScreen>(new NeuronId(Brain, "test.meddler", "m")).Dispatch(new Command(".", "test.meddle",
                JsonSerializer.SerializeToElement(new Meddle(free.ToString()), JsonSerializerOptions.Web)));
            await _os.Idle();
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(warnings); }

        // Forged IsOn=true would have made it signal Toggled(false) without storing; dropped, it acts uncontrolled and stores true.
        Assert.Equal([new Toggled(true)], Signals(free));
        Assert.True((await Drawn<ToggleSwitchView>(free)).IsOn);
        Assert.Contains(warnings.Lines, line => line.Contains("Dropped the props on control.toggle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Props_a_client_sends_on_the_wire_never_reach_the_control()
    {
        await Parent.Read();
        var forged = JsonSerializer.SerializeToElement(new ToggleSwitchProps("Forged", IsOn: true), JsonSerializerOptions.Web);

        // Through the parent the parent's own props for the slot replace them (uncontrolled "free" has none): the control stores.
        await Parent.Dispatch(new Command("free", "control.toggle", Props: forged));
        await _os.Idle();

        var free = Child("control.toggleSwitch", "free");
        Assert.True((await Drawn<ToggleSwitchView>(free)).IsOn);
        Assert.Equal([new Toggled(true)], Signals(free));
    }

    private sealed class WarningListener : System.Diagnostics.TraceListener
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public override void Write(string? message) { if (message is not null) { Lines.Enqueue(message); } }

        public override void WriteLine(string? message) => Write(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
