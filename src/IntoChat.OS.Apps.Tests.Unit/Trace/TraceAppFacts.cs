using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Trace;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Apps.Tests.Unit.Trace;

[Collection(nameof(TraceAppFacts))]
public sealed class TraceAppFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly NeuronId Ino = new(new BrainId("local"), "ino", "ino");
    private static readonly NeuronId Flows = new(new BrainId("local"), "flows", "flows/index");
    private static readonly NeuronId Agent = new(new BrainId("local"), "agent", "agent");

    [DigitalBrain.Signal("test.hop")]
    public sealed record Hop(string? FlowId = null) : DigitalBrain.Signal;

    [DigitalBrain.Signal("flow.test-step")]
    public sealed record FlowHop(string FlowId) : DigitalBrain.Signal;

    private static TraceEntry J(NeuronId publisher, long sequence, string? trace = "t1", (NeuronId, long)? cause = null, Signal? signal = null)
        => IntoChat.OS.Modules.Apps.Trace.TraceReaderNeuron.Entry(new JournaledSignal(publisher, sequence, signal ?? new Hop(), T0.AddMilliseconds(sequence * 10), trace, cause is { } c ? Causation.Address(c.Item1, c.Item2) : null));

    private static TraceEntry[] Script() =>
    [
        J(Ino, 1),
        J(Flows, 2, cause: (Ino, 1), signal: new FlowHop("local/flow.plan/1")),
        J(Agent, 3, cause: (Flows, 2)),
        J(Ino, 4, cause: (Ino, 1)),
        J(Agent, 5, cause: (Ino, 99)),
    ];

    [Fact]
    public async Task HopsFoldedByCausationIntoTree()
    {
        await using var os = FakeOs(Script());
        var trace = await os.Install<TraceApp>();
        await trace.Request("open", new TraceOpen("trace/t1"));

        var table = Assert.Single(Nodes(trace.App.Render()).OfType<DataTable>());
        Assert.Equal(["#", "Δt", "Publisher", "Signal", "Cause"], table.Columns);
        Assert.Equal(["1", "2", "3", "4", "5"], table.Rows.Select(r => r[0]));
        Assert.Equal([0, 2, 4, 2, 0], table.Rows.Select(r => r[2].Length - r[2].TrimStart().Length));
        var scene = Assert.Single(Nodes(trace.App.Render()).OfType<BrainSceneNode>());
        Assert.Equal(3, scene.Nodes.Count);
        Assert.Equal(3, scene.Synapses.Count);
        var flowButton = Assert.Single(Nodes(TraceViewRenderer.Side(TraceViewRenderer.Fold(Script()).Single(h => h.Sequence == 2))).OfType<Button>());
        Assert.Equal(new ButtonAction("apps/tasks", "open", ["local/flow.plan/1"]), flowButton.Action);
    }

    [Fact]
    public async Task EmptyStateExplainsMissingTraceIds()
    {
        await using var os = FakeOs([J(Ino, 1, trace: null), J(Agent, 2, trace: null)]);
        var trace = await os.Install<TraceApp>();
        await trace.Request("open", new TraceOpen("trace/t1"));

        Assert.Contains(Strings(trace.App.Render()), s => s.Contains("before tracing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackAndForwardMoveSelectedSequence()
    {
        await using var os = FakeOs(Script());
        var trace = await os.Install<TraceApp>();
        await trace.Request("open", new TraceOpen("trace/t1"));

        await trace.Request("back", new TraceBack());
        Assert.Equal(4, trace.App.State.SelectedSequence);
        Assert.False(trace.App.State.Live);
        await trace.Request("back", new TraceBack());
        await trace.Request("forward", new TraceForward());
        Assert.Equal(4, trace.App.State.SelectedSequence);
        var lit = Assert.Single(Assert.Single(Nodes(trace.App.Render()).OfType<BrainSceneNode>()).Synapses, s => s.Active);
        Assert.Equal((Ino.ToString(), Ino.ToString()), (lit.FromId, lit.ToId));
    }

    [Fact]
    public async Task LiveFollowsLatest()
    {
        await using var os = FakeOs(Script());
        var trace = await os.Install<TraceApp>();
        await trace.Request("open", new TraceOpen("trace/t1"));
        await trace.Request("back", new TraceBack());
        await trace.Request("back", new TraceBack());
        FakeReader.Signals = [.. Script(), J(Agent, 6, cause: (Agent, 5))];

        await trace.Request("live", new TraceLive());

        Assert.True(trace.App.State.Live);
        Assert.Equal(6, TraceViewRenderer.Selected(trace.App.State, TraceViewRenderer.Fold(FakeReader.Signals)));
        var lit = Assert.Single(Assert.Single(Nodes(trace.App.Render()).OfType<BrainSceneNode>()).Synapses, s => s.Active);
        Assert.Equal((Agent.ToString(), Agent.ToString()), (lit.FromId, lit.ToId));
    }

    [Fact]
    public async Task ProgressValueIsSelectedOverRange()
    {
        Assert.Equal(0, TraceViewRenderer.ProgressValue(7, 7, 7));
        Assert.Equal(0.5, TraceViewRenderer.ProgressValue(3, 1, 5));
        await using var os = FakeOs(Script());
        var trace = await os.Install<TraceApp>();
        await trace.Request("open", new TraceOpen("trace/t1"));
        await trace.Request("back", new TraceBack());
        await trace.Request("back", new TraceBack());

        var progress = Assert.Single(Nodes(trace.App.Render()).OfType<Progress>());
        Assert.Equal(0.5, progress.Value);
        var buttons = Nodes(trace.App.Render()).OfType<Button>().Select(b => b.Label).ToArray();
        Assert.Equal(["Play", "Pause", "Back", "Forward", "Live"], buttons.Where(b => b is "Play" or "Pause" or "Back" or "Forward" or "Live"));
    }

    private static TestOS FakeOs(TraceEntry[] signals)
    {
        FakeReader.Signals = signals;
        var os = TestOS.Local();
        os.Register<FakeReader>();
        return os;
    }

    private static IEnumerable<Node> Nodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Nodes(p.GetValue(node)))],
        View view => Nodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Nodes),
        _ => [],
    };

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [],
        string text => [text],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Strings),
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Strings(p.GetValue(value))),
        _ => [],
    };

    [Neuron("trace.reader")]
    private sealed class FakeReader : Neuron, ITraceReader
    {
        public static TraceEntry[] Signals { get; set; } = [];

        public Task<TracePage> ReadTrace(string traceId, long since, int limit)
        {
            var page = Signals.Where(s => s.Sequence > since).Take(limit).ToArray();
            return Task.FromResult(new TracePage(Signals.Length == 0 ? 0 : Signals.Min(s => s.Sequence), Signals.Length == 0 ? 0 : Signals.Max(s => s.Sequence), page));
        }
    }
}
