using System.Reflection;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Graph;
using DigitalBrain.Runtime.Apps;
using DigitalBrain.Spi;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// Each installed app joins the brain graph with its own shape: one Operation node per manifest operation, owned by the app
// (brain-graph-plan §1: derived from the stored manifest, never a second store). The app's subscriptions come from the
// runtime: a program's session synapses and in-process [On] / Watches() declarations are attributed to the app.
internal sealed class AppGraphSource(IServiceProvider services) : IGraphSource
{
    public async Task Contribute(BrainId brain, GraphSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var kernel = services.GetRequiredService<IBrainObserver>();
        var proxies = services.GetRequiredService<INeuronProxies>();
        var inProcess = services.GetService<InProcessAppHost>();
        List<(string Name, AppSnapshot Snapshot)> apps = [];
        try
        {
            await foreach (var instance in kernel.Instances<IApp>(cancellationToken).ConfigureAwait(false))
            {
                if (IApp.NameOf(brain.Value, instance.Key) is not { } name || sink.IndexOf(instance.Key) < 0) { continue; }
                apps.Add((name, await proxies.Get<IApp>(instance.Key).Read().WaitAsync(cancellationToken).ConfigureAwait(false)));
            }
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or UnauthorizedAccessException)
        {
            // No kernel or app neuron for this brain (a minimal host), or no right to read one: the apps stay plain nodes.
            return;
        }
        Contribute(brain, sink, apps);
        if (inProcess is not null)
        {
            foreach (var (name, _) in apps)
            {
                var key = IApp.Key(brain.Value, name);
                if (inProcess.TryGetTurns(key) is { } turns)
                {
                    ContributeSubscriptions(brain, sink, key, turns.Definition.SignalSubscriptions, turns.App.DeclaredWatches());
                }
            }
        }
    }

    internal static void Contribute(BrainId brain, GraphSink sink, IReadOnlyList<(string Name, AppSnapshot Snapshot)> apps)
    {
        foreach (var (name, snapshot) in apps)
        {
            var key = IApp.Key(brain.Value, name);
            var app = sink.IndexOf(key);
            if (app < 0) { continue; }
            foreach (var operation in snapshot.Manifest.Operations)
            {
                if (string.IsNullOrWhiteSpace(operation.Name)) { continue; }
                // "#" keeps the operation in its app's group (Grouping reads the id up to the hash).
                var node = sink.Node(key + "#" + operation.Name, GraphNodeKind.Operation, name + "/" + operation.Name);
                sink.Edge(app, node, GraphEdgeKind.Owns);
            }
        }
    }

    internal static void ContributeSubscriptions(
        BrainId brain,
        GraphSink sink,
        string appKey,
        IReadOnlyList<AppSignalSubscription> subscriptions,
        IReadOnlyList<AppWatch> watches)
    {
        var app = sink.IndexOf(appKey);
        if (app < 0)
        {
            app = sink.Node(appKey, GraphNodeKind.App, appKey);
        }

        foreach (var sub in subscriptions)
        {
            if (string.IsNullOrWhiteSpace(sub.SourceKey)) { continue; }
            var sourceId = ResolveSourceNeuronId(brain, sub.SourceKey, sub.ContractType);
            var sourceNode = sink.Node(sourceId, GraphNodeKind.Neuron, GraphBuilderLabelOf(sourceId));
            sink.Edge(app, sourceNode, GraphEdgeKind.Subscribes);
        }

        var address = new AppAddress(brain.Value, appKey);
        foreach (var watch in watches)
        {
            var rawKey = watch.Key(address);
            if (string.IsNullOrWhiteSpace(rawKey)) { continue; }
            var sourceId = ResolveSourceNeuronId(brain, rawKey, watch.SourceType);
            var sourceNode = sink.Node(sourceId, GraphNodeKind.Neuron, GraphBuilderLabelOf(sourceId));
            sink.Edge(app, sourceNode, GraphEdgeKind.Subscribes);
        }
    }

    private static string ResolveSourceNeuronId(BrainId brain, string sourceKey, Type? contractType)
    {
        if (NeuronId.TryParse(sourceKey, out var parsed) && parsed.Brain == brain)
        {
            return parsed.ToString();
        }
        var prefix = brain.Value + "/";
        var relativeKey = sourceKey.StartsWith(prefix, StringComparison.Ordinal)
            ? sourceKey[prefix.Length..]
            : sourceKey;
        var typeAlias = contractType?.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? contractType?.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? contractType?.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type;
        if (!string.IsNullOrWhiteSpace(typeAlias))
        {
            return new NeuronId(brain, typeAlias, relativeKey).ToString();
        }
        return sourceKey.StartsWith(prefix, StringComparison.Ordinal) ? sourceKey : prefix + relativeKey;
    }

    private static string GraphBuilderLabelOf(string id)
    {
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < id.Length - 1 ? id[(slash + 1)..] : id;
    }
}
