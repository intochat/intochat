using System.Text.Json;
using DigitalBrain.Neurons;
using DigitalBrain.Tests.Unit.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using Xunit;

namespace DigitalBrain.Tests.Unit.Screens;

public sealed record SlotParentState
{
    public int Pings { get; init; }
    public string[] Keys { get; init; } = ["editor"];
}

[Signal("slot.ping")]
public sealed record Ping : Signal;

[Neuron("app.slot-child")]
public sealed class SlotChildScreen : Screen<SampleCounterState>
{
    protected override View Render(SampleCounterState state) => new("Child", new Heading($"Pings: {state.Count}"));

    [On]
    public Task When(Ping signal) => Show(State with { Count = State.Count + 1 });

    public SampleCounterState Current => State;
}

[Neuron("app.slot-parent")]
public sealed class SlotParentScreen : Screen<SlotParentState>
{
    protected override View Render(SlotParentState state)
        => new("Parent", new Column([new Heading($"Pings: {state.Pings}"), .. state.Keys.Select(k => (Node)Slot<SlotChildScreen>(k))]));

    [On]
    public Task When(Ping signal) => Show(State with { Pings = State.Pings + 1 });

    public Task TestSend(Command command) => Send(command);
    public Task TestShow(SlotParentState next) => Show(next);
    public SlotParentState Current => State;
}

public sealed class ScreenSlotFacts
{
    private static readonly NeuronId ParentId = new(new BrainId("alice"), "app.slot-parent", "main");
    private static readonly NeuronId ChildId = new(new BrainId("alice"), "app.slot-child", ScreenExtensions.ChildAddress(ParentId, "editor"));

    private static async Task<(SlotParentScreen Parent, RecordingContext Context)> Attached()
    {
        var parent = new SlotParentScreen();
        var context = new RecordingContext(ParentId);
        NeuronAttachment.Attach(parent, context);
        await NeuronAttachment.Activate(parent, CancellationToken.None);
        return (parent, context);
    }

    private static async Task<SlotChildScreen> Child(RecordingContext context)
    {
        var child = new SlotChildScreen();
        NeuronAttachment.Attach(child, new RecordingContext(ChildId));
        await NeuronAttachment.Activate(child, CancellationToken.None);
        context.Neurons[ChildId] = child;
        return child;
    }

    [Fact]
    public async Task Slot_names_child_under_parent()
    {
        var (parent, context) = await Attached();
        var view = await parent.Read();

        Assert.NotNull(view);
        Assert.Equal("alice/app.slot-parent/main/editor", ChildId.Key);
        Assert.Contains(ChildId.ToString(), view.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"refKind\":\"slot\"", view.View.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(context.Gets);
    }

    [Fact]
    public async Task Send_to_dot_runs_own_request()
    {
        var (parent, _) = await Attached();

        await parent.TestSend(new Command(".", "slot.ping"));

        Assert.Equal(1, parent.Current.Pings);
    }

    [Fact]
    public async Task Send_to_child_runs_child_request()
    {
        var (parent, context) = await Attached();
        var child = await Child(context);

        await parent.TestSend(new Command("./editor", "slot.ping"));
        await parent.TestSend(new Command("editor", "slot.ping"));

        Assert.Equal(2, child.Current.Count);
        Assert.Equal([ChildId, ChildId], context.Gets);
    }

    [Fact]
    public async Task Dispatch_routes_through_the_parent_gate()
    {
        var (parent, context) = await Attached();
        var child = await Child(context);

        await parent.Dispatch(new Command("editor", "slot.ping"));
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => parent.Dispatch(new Command("ghost", "slot.ping")));

        Assert.Equal(1, child.Current.Count);
        Assert.Equal(ActivationRefusedException.UnknownChild, refused.Reason);
        Assert.Equal([ChildId], context.Gets);
    }

    [Fact]
    public async Task Send_to_unknown_child_is_refused_and_creates_nothing()
    {
        var (parent, context) = await Attached();

        var pending = parent.TestSend(new Command("ghost", "slot.ping"));
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => pending);

        Assert.Equal(ActivationRefusedException.UnknownChild, refused.Reason);
        Assert.Empty(context.Gets);

        await parent.TestShow(parent.Current with { Keys = [] });
        refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => parent.TestSend(new Command("editor", "slot.ping")));
        Assert.Equal(ActivationRefusedException.UnknownChild, refused.Reason);
        Assert.Empty(context.Gets);
    }

    [Fact]
    public async Task Duplicate_slot_keys_in_one_view_throw_at_render()
    {
        var (parent, _) = await Attached();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => parent.TestShow(parent.Current with { Keys = ["a", "a"] }));

        Assert.Contains("'a'", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await parent.Read())!.Generation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("/")]
    public async Task Bad_slot_keys_are_rejected_at_render(string key)
    {
        var (parent, _) = await Attached();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => parent.TestShow(parent.Current with { Keys = [key] }));

        Assert.Contains($"'{key}'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("./a/b")]
    [InlineData("..")]
    [InlineData("./.")]
    [InlineData("./")]
    public async Task Bad_send_targets_are_rejected_without_a_get(string target)
    {
        var (parent, context) = await Attached();

        var pending = parent.TestSend(new Command(target, "slot.ping"));

        await Assert.ThrowsAsync<ArgumentException>(() => pending);
        Assert.Empty(context.Gets);
    }

    [Fact]
    public void Child_addresses_cannot_collide_across_parents()
    {
        var brain = new BrainId("alice");
        var outer = new NeuronId(brain, "app.slot-parent", "main");
        var inner = new NeuronId(brain, "app.slot-parent", "main/a");

        Assert.Throws<ArgumentException>(() => ScreenExtensions.ChildAddress(outer, "a/b"));
        Assert.Equal("alice/app.slot-parent/main/a/b", ScreenExtensions.ChildAddress(inner, "b"));
    }
}
