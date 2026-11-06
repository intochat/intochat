using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using DigitalBrain.Graph;
using DigitalBrain.Spi;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Agents;

// The agents' runs as graph nodes: each run is owned by the brain and works on its agent while it runs
// (heavier once it has run past the stuck threshold), and produced the agent's reply once it completed.
public sealed class RunGraphSource(IServiceProvider services) : IGraphSource
{
    public const float StuckRunThresholdMinutes = 5f;

    public async Task Contribute(BrainId brain, GraphSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var observer = services.GetService<IBrainObserver>();
        var proxies = services.GetService<INeuronProxies>();
        if (observer is null || proxies is null) { return; }

        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var prefix = brain.Value + "/";
        var runs = new List<(string Agent, AgentRun Run)>();

        await foreach (var instance in observer.Instances("agent", cancellationToken).ConfigureAwait(false))
        {
            if (!instance.Key.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            var id = NeuronId.TryParse(instance.Key, out var parsed)
                ? parsed
                : new NeuronId(brain, "agent", instance.Key[prefix.Length..]);
            AgentState state;
            try { state = await proxies.Get<IAgent>(id).Read().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            foreach (var run in state.ActiveRun is null ? state.Runs : [.. state.Runs, state.ActiveRun])
            { runs.Add((id.ToString(), run)); }
        }

        Contribute(brain, sink, runs, clock.GetUtcNow());
    }

    public static void Contribute(BrainId brain, GraphSink sink, IReadOnlyList<(string Agent, AgentRun Run)> runs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(runs);

        var root = sink.IndexOf(brain.Value);
        foreach (var (agent, run) in runs)
        {
            var runNode = sink.Node($"{agent}#run/{run.RunId}", GraphNodeKind.Run, $"run/{run.RunId}");
            if (root >= 0) { sink.Edge(root, runNode, GraphEdgeKind.Owns); }

            var agentNode = sink.IndexOf(agent) is var existing and >= 0 ? existing : sink.Node(agent, GraphNodeKind.Neuron, agent);
            if (run.Status == AgentRunStatus.Running)
            {
                var elapsedMinutes = (float)Math.Max(0, (now - run.StartedAt).TotalMinutes);
                var weight = elapsedMinutes >= StuckRunThresholdMinutes ? StuckRunThresholdMinutes : 1f;
                sink.Edge(runNode, agentNode, GraphEdgeKind.WorksOn, weight);
            }
            else if (run.Status == AgentRunStatus.Completed)
            {
                sink.Edge(runNode, agentNode, GraphEdgeKind.Produced, 1f);
            }
        }
    }
}
