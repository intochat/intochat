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
        await FollowSlot<ToggleSwitchNeuron, Toggled>("dark");
        await FollowSlot<ComboBoxNeuron, SelectionChanged>("theme");
    }

    protected override View Render(ControlledParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Slot<ToggleSwitchNeuron, ToggleSwitchProps>("dark", new ToggleSwitchProps("Dark mode", IsOn: state.DarkMode)),
            Slot<ComboBoxNeuron, ComboBoxProps>("theme", new ComboBoxProps("Theme", [new ComboBoxOption("light", "Light"), new ComboBoxOption("dark", "Dark")], SelectedId: state.Theme)),
            Slot<ToggleSwitchNeuron, ToggleSwitchProps>("free", new ToggleSwitchProps("Free")),
        ]));
    }

    [On]
    public Task OnToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        return SignalSlot == "dark" ? Show(State with { DarkMode = toggled.IsOn }) : Task.CompletedTask;
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return SignalSlot == "theme" ? Show(State with { Theme = changed.Id }) : Task.CompletedTask;
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

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
