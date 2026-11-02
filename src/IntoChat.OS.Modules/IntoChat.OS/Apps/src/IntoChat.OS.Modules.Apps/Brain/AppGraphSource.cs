using DigitalBrain;
using DigitalBrain.Graph;
using DigitalBrain.Spi;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// Each installed app joins the brain graph with its own shape: one Operation node per manifest operation, owned by the app
// (brain-graph-plan §1: derived from the stored manifest, never a second store). The app's subscriptions come from the
// runtime: a program's session synapses are attributed to the app by the graph builder.
internal sealed class AppGraphSource(IServiceProvider services) : IGraphSource
{
    public async Task Contribute(BrainId brain, GraphSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var kernel = services.GetRequiredService<IBrainObserver>();
        var proxies = services.GetRequiredService<INeuronProxies>();
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
}
