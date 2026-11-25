using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;

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
        return Get<IScreen>(NeuronId.Parse(meddle.Child)).Dispatch(new Command(".", "control.setChecked", JsonSerializer.SerializeToElement(new SetChecked(false), JsonSerializerOptions.Web),
            Props: JsonSerializer.SerializeToElement(new ToggleSwitchProps("Forged", IsOn: true), JsonSerializerOptions.Web)));
    }
}

// A controlled control (the parent passes its value as a prop) only signals the person's change, as the absolute value the command
// asked for and every time; the parent stores it and re-renders. Without a value prop the control stores the value itself. A child
// takes commands only from its parent: anything else sending one straight to it is refused.
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

    private static Command Command(string target, Signal command)
        => new(target, command.GetType().GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias,
            JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web));

    private async Task Send(string key, Signal command)
    {
        await Parent.Dispatch(Command(key, command));
        await _os.Idle();
    }

    [Fact]
    public async Task A_controlled_toggle_signals_the_new_value_stores_nothing_and_the_parent_rerenders_it()
    {
        await Parent.Read();

        await Send("dark", new SetChecked(false));

        var dark = Child("control.toggleSwitch", "dark");
        Assert.Equal([new Toggled(false)], Signals(dark));
        Assert.False((await Drawn<ToggleSwitchView>(dark)).IsOn);
        Assert.False((await SlotProps("dark")).GetProperty("isOn").GetBoolean());

        await Send("dark", new SetChecked(true));

        Assert.Equal([new Toggled(false), new Toggled(true)], Signals(dark));
        Assert.True((await SlotProps("dark")).GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task Two_fast_commands_on_a_controlled_toggle_reach_the_parent_as_two_absolute_values()
    {
        await Parent.Read();

        // Sent back to back: the second arrives before the parent has re-rendered the first, so the props still say on.
        await Parent.Dispatch(Command("dark", new SetChecked(false)));
        await Parent.Dispatch(Command("dark", new SetChecked(true)));
        await _os.Idle();

        Assert.Equal([new Toggled(false), new Toggled(true)], Signals(Child("control.toggleSwitch", "dark")));
        Assert.True((await SlotProps("dark")).GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task A_controlled_value_equal_to_the_drawn_one_is_still_published()
    {
        await Parent.Read();

        await Send("dark", new SetChecked(true));
        await Send("theme", new DigitalBrain.UI.Commands.Select("light"));

        Assert.Equal([new Toggled(true)], Signals(Child("control.toggleSwitch", "dark")));
        Assert.Equal([new SelectionChanged("light")], Signals(Child("control.comboBox", "theme")));
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

        await Send("free", new SetChecked(true));

        var free = Child("control.toggleSwitch", "free");
        Assert.Equal([new Toggled(true)], Signals(free));
        Assert.True((await Drawn<ToggleSwitchView>(free)).IsOn);
    }

    [Fact]
    public async Task A_command_from_a_neuron_that_is_not_the_parent_is_refused()
    {
        await Parent.Read();
        var free = Child("control.toggleSwitch", "free");

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => _os.Get<IScreen>(new NeuronId(Brain, "test.meddler", "m"))
            .Dispatch(new Command(".", "test.meddle", JsonSerializer.SerializeToElement(new Meddle(free.ToString()), JsonSerializerOptions.Web))));
        await _os.Idle();

        Assert.Equal(ControlProps.NotFromParent, refused.Reason);
        Assert.Empty(Signals(free));
    }

    [Fact]
    public async Task A_command_sent_straight_to_a_child_from_outside_is_refused()
    {
        await Parent.Read();
        var dark = Child("control.toggleSwitch", "dark");

        var dispatched = await Assert.ThrowsAsync<ActivationRefusedException>(() => _os.Get<IScreen>(dark).Dispatch(Command(".", new SetChecked(false))));
        // A request is one-way, so its refusal reaches no caller: the control publishes nothing.
        await _os.Get<IScreen>(dark).Request("control.setChecked", JsonSerializer.SerializeToElement(new SetChecked(false), JsonSerializerOptions.Web));
        await _os.Idle();

        Assert.Equal(ControlProps.NotFromParent, dispatched.Reason);
        Assert.Empty(Signals(dark));
        Assert.True((await SlotProps("dark")).GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task Props_a_client_sends_on_the_wire_never_reach_the_control()
    {
        await Parent.Read();
        var forged = JsonSerializer.SerializeToElement(new ToggleSwitchProps("Forged", IsOn: true), JsonSerializerOptions.Web);

        // Through the parent the parent's own props for the slot replace them (uncontrolled "free" has none): the control stores.
        await Parent.Dispatch(Command("free", new SetChecked(true)) with { Props = forged });
        await _os.Idle();

        var free = Child("control.toggleSwitch", "free");
        Assert.True((await Drawn<ToggleSwitchView>(free)).IsOn);
        Assert.Equal([new Toggled(true)], Signals(free));
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
