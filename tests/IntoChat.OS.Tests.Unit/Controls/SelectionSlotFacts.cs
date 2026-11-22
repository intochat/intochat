using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using SelectCommand = DigitalBrain.UI.Commands.Select;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record SelectionParentState
{
    public bool PrivacyRemoved { get; init; }
}

[Signal("test.removePrivacy")]
public sealed record RemovePrivacy : Signal;

/*
# Selection parent

A test parent that slots each selecting control with two items in its props.
*/
[Neuron("test.selection-parent")]
public sealed class SelectionParent : Screen<SelectionParentState>
{
    protected override View Render(SelectionParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Draw(state.PrivacyRemoved);
    }

    [On]
    public Task RemovePrivacy(RemovePrivacy signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Show(State with { PrivacyRemoved = true });
    }

    private View Draw(bool privacyRemoved) => new("Parent", new Column([
        Child<TabStripNeuron, TabStripProps>("tabs", new TabStripProps(privacyRemoved
            ? [new TabStripItem("general", "General"), new TabStripItem("about", "About")]
            : [new TabStripItem("general", "General"), new TabStripItem("privacy", "Privacy")])),
        Child<ComboBoxNeuron, ComboBoxProps>("theme", new ComboBoxProps("Theme", [new ComboBoxOption("light", "Light"), new ComboBoxOption("dark", "Dark")])),
        Child<ListBoxNeuron, ListBoxProps>("devices", new ListBoxProps([new ListBoxItem("laptop", "Laptop"), new ListBoxItem("phone", "Phone", "Yesterday")])),
        Child<ListBoxNeuron, ListBoxProps>("history", new ListBoxProps([new ListBoxItem("1", "Signed in")], ListSelectionMode.None)),
    ]));
}

// The items of a selecting control are its parent's props, so the parent's routing hop checks a Select against them: an id naming no
// item is refused with a coded reason before the child is reached. A known id reaches the child.
public sealed class SelectionSlotFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.selection-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public SelectionSlotFacts()
    {
        _os.Register<SelectionParent>();
        _os.Register<TabStripNeuron>();
        _os.Register<ComboBoxNeuron>();
        _os.Register<ListBoxNeuron>();
    }

    private IScreen Parent => _os.Get<IScreen>(ParentId);

    private static NeuronId Child(string type, string key) => new(Brain, type, ScreenExtensions.ChildAddress(ParentId, key));

    private Task Select(string key, string id)
        => Parent.Dispatch(new Command(key, "control.select", JsonSerializer.SerializeToElement(new SelectCommand(id), JsonSerializerOptions.Web)));

    private Signal[] Signals(NeuronId child) => [.. _os.Journal(Brain).Signals(child).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task Select_unknown_is_refused_on_tabs()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Select("tabs", "nope"));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
        Assert.False(_os.IsActive(Child("control.tabStrip", "tabs")));
    }

    [Fact]
    public async Task Select_without_an_id_is_refused_on_tabs()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Parent.Dispatch(new Command("tabs", "control.select")));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
    }

    [Fact]
    public async Task A_known_tab_reaches_the_child()
    {
        await Select("tabs", "privacy");

        Assert.Equal([new SelectionChanged("privacy")], Signals(Child("control.tabStrip", "tabs")));
    }

    [Fact]
    public async Task Select_unknown_is_refused_on_a_combo_box()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Select("theme", "sepia"));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
        Assert.False(_os.IsActive(Child("control.comboBox", "theme")));
    }

    [Fact]
    public async Task A_known_option_reaches_the_combo_box()
    {
        await Select("theme", "dark");

        Assert.Equal([new SelectionChanged("dark")], Signals(Child("control.comboBox", "theme")));
    }

    [Fact]
    public async Task Select_unknown_is_refused_on_a_list_box()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Select("devices", "watch"));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
        Assert.False(_os.IsActive(Child("control.listBox", "devices")));
    }

    [Fact]
    public async Task A_known_item_reaches_the_list_box()
    {
        await Select("devices", "phone");

        Assert.Equal([new SelectionChanged("phone")], Signals(Child("control.listBox", "devices")));
    }

    [Fact]
    public async Task A_list_box_in_none_mode_refuses_every_select()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Select("history", "1"));

        Assert.Equal(SelectionRefusals.SelectionOff, refused.Reason);
        Assert.False(_os.IsActive(Child("control.listBox", "history")));
    }

    [Fact]
    public async Task After_the_props_drop_the_selected_tab_selecting_a_valid_one_publishes()
    {
        await Select("tabs", "privacy");
        await Parent.Dispatch(new Command(".", "test.removePrivacy"));

        await Select("tabs", "general");

        Assert.Equal([new SelectionChanged("privacy"), new SelectionChanged("general")], Signals(Child("control.tabStrip", "tabs")));
    }

    [Fact]
    public void The_gate_names_the_select_command_alias()
        => Assert.Equal(SelectionRefusals.SelectSignal, typeof(SelectCommand).GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias);

    [Fact]
    public void A_gate_passes_every_other_command()
        => Assert.Null(new ListBoxProps([], ListSelectionMode.None).RefusalOf("control.toggle", null));

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
