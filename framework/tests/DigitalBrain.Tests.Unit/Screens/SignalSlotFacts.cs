using System.Collections.Concurrent;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;

namespace DigitalBrain.Tests.Unit.Screens;

[Signal("test.slotPing")]
public sealed record SlotPing(string Tag) : Signal;

[Neuron("test.slot-sender-parent")]
public sealed class SlotSenderParent : Screen<SampleCounterState>
{
    public ConcurrentDictionary<string, (string? Before, string? After)> Seen { get; } = new();

    public ConcurrentDictionary<string, TaskCompletionSource> Gates { get; } = new();

    protected override View Render(SampleCounterState state) => new("Parent", new Text("parent"));

    [On]
    public async Task Ping(SlotPing ping)
    {
        ArgumentNullException.ThrowIfNull(ping);
        var before = SignalChild;
        await Gates[ping.Tag].Task;
        Seen[ping.Tag] = (before, SignalChild);
    }

    public string? SlotOutsideDelivery => SignalChild;
}

// The slot a followed signal came from is scoped to its delivery, not stored on the neuron: interleaved deliveries each see their own.
public sealed class SignalSlotFacts
{
    private static readonly NeuronId ParentId = new(new BrainId("b1"), "test.slot-sender-parent", "main");

    private static JournaledSignal From(string slot, string tag)
        => new(new NeuronId(ParentId.Brain, "control.toggleSwitch", ScreenExtensions.ChildAddress(ParentId, slot)), 1, new SlotPing(tag), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Interleaved_deliveries_each_see_their_own_slot()
    {
        var parent = new SlotSenderParent();
        NeuronAttachment.Attach(parent, new FakeNeuronContext(ParentId));
        parent.Gates["a"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.Gates["b"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Reactions.Dispatch(parent, From("left", "a"), CancellationToken.None);
        var second = Reactions.Dispatch(parent, From("right", "b"), CancellationToken.None);
        parent.Gates["b"].SetResult();
        await second;
        parent.Gates["a"].SetResult();
        await first;

        Assert.Equal(("left", "left"), parent.Seen["a"]);
        Assert.Equal(("right", "right"), parent.Seen["b"]);
        Assert.Null(parent.SlotOutsideDelivery);
    }

    [Fact]
    public async Task A_signal_from_a_neuron_that_is_not_a_slot_names_no_slot()
    {
        var parent = new SlotSenderParent();
        NeuronAttachment.Attach(parent, new FakeNeuronContext(ParentId));
        parent.Gates["c"] = new TaskCompletionSource();
        parent.Gates["c"].SetResult();

        await Reactions.Dispatch(parent, new JournaledSignal(new NeuronId(ParentId.Brain, "other", "x"), 1, new SlotPing("c"), DateTimeOffset.UnixEpoch), CancellationToken.None);

        Assert.Equal((null, null), parent.Seen["c"]);
    }
}
