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

public sealed record PropsParentState
{
    public string Label { get; init; } = "Terms";

    public bool TermsEnabled { get; init; } = true;

    public bool NestSlot { get; init; }
}

/*
# Props parent

A test parent that slots a check box with props from its own state, and a plain slot without props.
*/
[Neuron("test.props-parent")]
public sealed class PropsParent : Screen<PropsParentState>
{
    protected override View Render(PropsParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<CheckBoxNeuron, CheckBoxProps>("terms", new CheckBoxProps(state.Label, state.TermsEnabled)),
            Child<CheckBoxNeuron>("plain"),
            .. state.NestSlot
                ? new Node[] { Child<ExpanderNeuron, ExpanderProps>("more", new ExpanderProps("More", [new Text("Data is fine"), Child<CheckBoxNeuron>("inner")])) }
                : [],
        ]));
    }

    [On]
    public Task NestSlot(NestSlot signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Show(State with { NestSlot = true });
    }

    [On]
    public Task Relabel(Relabel signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Show(State with { Label = signal.Label });
    }

    [On]
    public Task EnableTerms(EnableTerms signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Show(State with { TermsEnabled = signal.IsEnabled });
    }
}

[Signal("test.relabel")]
public sealed record Relabel(string Label) : Signal;

[Signal("test.nestSlot")]
public sealed record NestSlot : Signal;

[Signal("test.enableTerms")]
public sealed record EnableTerms(bool IsEnabled) : Signal;

// Props are the parent's: written on the slot node, re-rendered with the parent, and the parent's routing hop refuses a command to a
// slot whose props are disabled. The child stores only what the user changes, and rendering a slot creates nothing.
public sealed class SlotPropsFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.props-parent", "main");
    private static readonly NeuronId ChildId = new(Brain, "control.checkBox", ScreenExtensions.ChildAddress(ParentId, "terms"));
    private static readonly NeuronId PlainId = new(Brain, "control.checkBox", ScreenExtensions.ChildAddress(ParentId, "plain"));

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public SlotPropsFacts()
    {
        _os.Register<PropsParent>();
        _os.Register<CheckBoxNeuron>();
    }

    private IScreen Parent => _os.Get<IScreen>(ParentId);

    private Task Tell<TSignal>(TSignal signal) where TSignal : Signal
        => Parent.Dispatch(new Command(".", typeof(TSignal).GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias, JsonSerializer.SerializeToElement(signal, JsonSerializerOptions.Web)));

    private async Task<PublishedView> ChildView()
        => await _os.Get<IScreen>(ChildId).Read() ?? throw new InvalidOperationException("The child has no view.");

    // The props the parent's current view writes on the slot of `child`.
    private async Task<CheckBoxProps> SlotProps(NeuronId child)
    {
        var view = await Parent.Read() ?? throw new InvalidOperationException("The parent has no view.");
        var slot = view.View.GetProperty("root").GetProperty("children").EnumerateArray()
            .Single(node => node.GetProperty("neuronUri").GetString() == child.ToString());
        return slot.GetProperty("props").Deserialize<CheckBoxProps>(JsonSerializerOptions.Web)!;
    }

    private Signal[] ChildSignals() => [.. _os.Journal(Brain).Signals(ChildId).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task Rendering_a_slot_creates_nothing()
    {
        Assert.NotNull(await Parent.Read());

        Assert.False(_os.IsActive(ChildId));
        Assert.False(_os.IsActive(PlainId));
        Assert.Equal(new CheckBoxProps("Terms"), await SlotProps(ChildId));
    }

    [Fact]
    public async Task An_enabled_slot_passes_the_command_to_the_child()
    {
        await Parent.Dispatch(new Command("terms", "control.toggle"));

        Assert.Equal([new Toggled(true)], ChildSignals());
    }

    [Fact]
    public async Task The_parent_refuses_a_command_to_a_disabled_slot_and_the_child_is_not_called()
    {
        await Tell(new EnableTerms(false));

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Parent.Dispatch(new Command("terms", "control.toggle")));

        Assert.Equal(ControlProps.Disabled, refused.Reason);
        Assert.False(_os.IsActive(ChildId));
        Assert.False((await SlotProps(ChildId)).IsEnabled);
    }

    [Fact]
    public async Task Changing_props_rerenders_the_label_without_touching_the_child()
    {
        await Parent.Dispatch(new Command("terms", "control.toggle"));
        var before = await ChildView();

        await Tell(new Relabel("Renamed"));

        Assert.Equal("Renamed", (await SlotProps(ChildId)).Label);
        Assert.Equal(before.Generation, (await ChildView()).Generation);
        Assert.Equal([new Toggled(true)], ChildSignals());
    }

    [Fact]
    public async Task The_child_state_survives_the_parent_changing_props()
    {
        await Parent.Dispatch(new Command("terms", "control.toggle"));

        await Tell(new EnableTerms(false));
        await Tell(new Relabel("Renamed"));
        await Tell(new EnableTerms(true));

        var drawn = (await ChildView()).View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web);
        Assert.Equal(new CheckBoxView("terms", true), drawn);
    }

    [Fact]
    public async Task A_direct_call_to_the_child_is_not_gated_by_the_parent_props()
    {
        await Tell(new EnableTerms(false));

        await _os.Get<IScreen>(ChildId).Dispatch(new Command(".", "control.toggle"));

        Assert.Equal([new Toggled(true)], ChildSignals());
    }

    [Fact]
    public async Task A_slot_inside_control_props_is_refused_at_render()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Tell(new NestSlot()));

        Assert.Contains("child 'more'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("data nodes only", refused.Message, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
