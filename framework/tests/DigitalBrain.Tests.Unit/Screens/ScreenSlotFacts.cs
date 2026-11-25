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

[Signal("slot.boom")]
public sealed record Boom : Signal;

[Neuron("app.slot-grandparent")]
public sealed class SlotGrandparentScreen : Screen<SampleCounterState>
{
    protected override View Render(SampleCounterState state) => new("Grandparent", new Column([Child<SlotParentScreen>("mid")]));
}

[Neuron("app.slot-child")]
public sealed class SlotChildScreen : Screen<SampleCounterState>
{
    protected override View Render(SampleCounterState state) => new("Child", new Heading($"Pings: {state.Count}"));

    [On]
    public Task When(Ping signal) => Show(State with { Count = State.Count + 1 });

    [On]
    public Task When(Boom signal) => throw new InvalidOperationException($"{Alias} could not toggle");

    public SampleCounterState Current => State;
}

[Neuron("app.slot-parent")]
public sealed class SlotParentScreen : Screen<SlotParentState>
{
    protected override View Render(SlotParentState state)
        => new("Parent", new Column([new Heading($"Pings: {state.Pings}"), .. state.Keys.Select(k => (Node)Child<SlotChildScreen>(k))]));

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
        // The runtime stamps the hop: the child is called from inside the cluster, through its parent's proxy.
        NeuronAttachment.Attach(child, new RecordingContext(ChildId) { Caller = DigitalBrain.Access.Caller.OfKernel(ChildId.Brain, via: ParentId) });
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
    public async Task Dispatch_with_a_key_path_reaches_the_grandchild_hop_by_hop()
    {
        var grandId = new NeuronId(new BrainId("alice"), "app.slot-grandparent", "main");
        var midId = new NeuronId(new BrainId("alice"), "app.slot-parent", ScreenExtensions.ChildAddress(grandId, "mid"));
        var leafId = new NeuronId(new BrainId("alice"), "app.slot-child", ScreenExtensions.ChildAddress(midId, "editor"));
        var grand = new SlotGrandparentScreen();
        var grandContext = new RecordingContext(grandId);
        NeuronAttachment.Attach(grand, grandContext);
        await NeuronAttachment.Activate(grand, CancellationToken.None);
        var mid = new SlotParentScreen();
        var midContext = new RecordingContext(midId) { Caller = DigitalBrain.Access.Caller.OfKernel(midId.Brain, via: grandId) };
        NeuronAttachment.Attach(mid, midContext);
        await NeuronAttachment.Activate(mid, CancellationToken.None);
        grandContext.Neurons[midId] = mid;
        var leaf = new SlotChildScreen();
        NeuronAttachment.Attach(leaf, new RecordingContext(leafId) { Caller = DigitalBrain.Access.Caller.OfKernel(leafId.Brain, via: midId) });
        await NeuronAttachment.Activate(leaf, CancellationToken.None);
        midContext.Neurons[leafId] = leaf;

        await grand.Dispatch(new Command("mid/editor", "slot.ping"));
        Assert.Equal(1, leaf.Current.Count);

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => grand.Dispatch(new Command("ghost/editor", "slot.ping")));
        Assert.Equal(ActivationRefusedException.UnknownChild, refused.Reason);
        refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => grand.Dispatch(new Command("mid/ghost", "slot.ping")));
        Assert.Equal(ActivationRefusedException.UnknownChild, refused.Reason);
        Assert.Equal([midId, midId], grandContext.Gets);
        Assert.Equal([leafId], midContext.Gets);
    }

    [Fact]
    public async Task A_child_refuses_a_command_or_request_whose_caller_is_not_its_parent()
    {
        var stranger = new NeuronId(new BrainId("alice"), "app.slot-parent", "other");
        foreach (var caller in new DigitalBrain.Access.Caller?[] { null, DigitalBrain.Access.Caller.OfKernel(ChildId.Brain), DigitalBrain.Access.Caller.OfKernel(ChildId.Brain, via: stranger) })
        {
            var child = new SlotChildScreen();
            NeuronAttachment.Attach(child, new RecordingContext(ChildId) { Caller = caller });
            await NeuronAttachment.Activate(child, CancellationToken.None);

            var dispatched = await Assert.ThrowsAsync<ActivationRefusedException>(() => child.Dispatch(new Command(".", "slot.ping")));
            var requested = await Assert.ThrowsAsync<ActivationRefusedException>(() => child.Request("slot.ping"));

            Assert.Equal((ControlProps.NotFromParent, ControlProps.NotFromParent), (dispatched.Reason, requested.Reason));
            Assert.Equal(0, child.Current.Count);
        }
    }

    [Fact]
    public async Task Dispatch_awaits_the_child_so_its_failure_reaches_the_caller()
    {
        var (parent, context) = await Attached();
        _ = await Child(context);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => parent.Dispatch(new Command("editor", "slot.boom")));

        Assert.Contains("could not toggle", failed.Message, StringComparison.Ordinal);
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
    [InlineData("editor//b")]
    [InlineData("./editor/../b")]
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
