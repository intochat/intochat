using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Graph;
using DigitalBrain.Neurons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps.Flows;

// The brain's unfinished flows as graph nodes (id = flow id, label = title), owned by the brain, with an edge per relation
// between two shown flows. The graph has no flow node kind; a flow shows as a Task.
internal sealed class FlowGraphSource(IServiceProvider services) : IGraphSource
{
    private static readonly FlowStatus[] Open = [FlowStatus.Pending, FlowStatus.Running, FlowStatus.Waiting, FlowStatus.Blocked];

    public async Task Contribute(BrainId brain, GraphSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (services.GetService<INeuronProxies>() is not { } proxies) { return; }
        FlowView[] flows;
        try { flows = await proxies.Get<IFlows>(IFlows.Key(brain.Value)).List(new FlowQuery(Limit: FlowsState.KeepFinished, AnyStatus: Open), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            FlowsLog.GraphUnlisted(services.GetService<ILoggerFactory>()?.CreateLogger<FlowGraphSource>() ?? (ILogger)NullLogger.Instance, error, brain.Value);
            return;
        }
        Contribute(brain, sink, flows);
    }

    public static void Contribute(BrainId brain, GraphSink sink, IReadOnlyList<FlowView> flows)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(flows);
        var open = flows.Where(flow => flow.Status is not (FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled)).ToArray();
        var root = sink.IndexOf(brain.Value);
        foreach (var flow in open)
        {
            var node = sink.Node(flow.FlowId, GraphNodeKind.Task, flow.Title);
            if (root >= 0) { sink.Edge(root, node, GraphEdgeKind.Owns); }
        }
        foreach (var flow in open)
        {
            var from = sink.IndexOf(flow.FlowId);
            foreach (var relation in flow.Relations)
            {
                if (sink.IndexOf(relation.TargetFlowId) is var to and >= 0) { Edge(sink, from, to, relation.Kind); }
            }
        }
    }

    private static void Edge(GraphSink sink, int flow, int target, RelationKind kind)
    {
        switch (kind)
        {
            case RelationKind.BlockedBy: sink.Edge(target, flow, GraphEdgeKind.Blocks); break;
            case RelationKind.Blocks: sink.Edge(flow, target, GraphEdgeKind.Blocks); break;
            case RelationKind.Subtask: sink.Edge(flow, target, GraphEdgeKind.Subtask); break;
            case RelationKind.Parent: sink.Edge(target, flow, GraphEdgeKind.Subtask); break;
            default: sink.Edge(flow, target, GraphEdgeKind.Relates); break;
        }
    }
}
