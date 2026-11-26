using DigitalBrain;
using DigitalBrain.Graph;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

// Each installed app joins the brain graph with its own shape: one Operation node per manifest operation, owned by the app
// (brain-graph-plan §1: derived from the stored manifest, never a second store). Screen apps register live runtime synapses
// via Follow, which GraphBuilder projects directly from IBrainObserver.Wires.
internal sealed class AppGraphSource(IServiceProvider services) : IGraphSource
{
    public async Task Contribute(BrainId brain, GraphSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var edge = services.GetService<DigitalBrain.Access.EdgeAuthority>();
        using var _ = edge?.Enter(edge.Module("apps.graph", brain));
        var kernel = services.GetService<IBrainObserver>();
        var proxies = services.GetService<INeuronProxies>();
        List<(string Name, AppSnapshot Snapshot)> apps = [];
        if (kernel is not null)
        {
            try
            {
                await foreach (var instance in kernel.Instances<IApp>(cancellationToken).ConfigureAwait(false))
                {
                    if (!IntoChat.OS.Host.Primitives.AppRef.TryParse(brain, instance.Key, out var appRef) || sink.IndexOf(instance.Key) < 0) { continue; }
                    var name = appRef.Name;
                    var appProxy = proxies is not null
                        ? proxies.Get<IApp>(instance.Key)
                        : SiloNeurons.Get<IApp>(services, instance.Key);
                    apps.Add((name, await appProxy.Read().WaitAsync(cancellationToken).ConfigureAwait(false)));
                }
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or UnauthorizedAccessException)
            {
                // No kernel or app neuron for this brain (a minimal host), or no right to read one: fall back to compiled registrations.
                (services.GetService<ILoggerFactory>()?.CreateLogger<AppGraphSource>() ?? OsConsoleLog.For<AppGraphSource>()).LogSwallowed(error, "App instances unreadable for brain graph; falling back to compiled registrations.");
            }
        }

        var rootIdx = sink.IndexOf(brain.Value);
        foreach (var assembly in services.GetServices<CompiledAppsRegistration>().Select(r => r.Assembly).Append(typeof(AppsModule).Assembly).Distinct())
        {
            foreach (var compiled in CompiledApps.Of(assembly))
            {
                var name = compiled.Bundle.Name;
                var appRef = IntoChat.OS.Host.Primitives.AppRef.Of(brain, name);
                var key = appRef.Key;
                if (sink.IndexOf(key) < 0)
                {
                    var nodeIdx = sink.Node(key, GraphNodeKind.App, string.IsNullOrWhiteSpace(compiled.Bundle.Manifest.Title) ? appRef.RelativePath : compiled.Bundle.Manifest.Title);
                    if (rootIdx >= 0) { sink.Edge(rootIdx, nodeIdx, GraphEdgeKind.Owns); }
                }
                if (apps.All(a => !string.Equals(a.Name, name, StringComparison.Ordinal)))
                {
                    apps.Add((name, new AppSnapshot(
                        name,
                        compiled.Bundle.Manifest,
                        compiled.Bundle.Spec,
                        [],
                        [],
                        [],
                        compiled.Bundle.Version,
                        AppStatus.Live,
                        [],
                        [])));
                }
            }
        }

        Contribute(brain, sink, apps);
    }

    internal static void Contribute(BrainId brain, GraphSink sink, IReadOnlyList<(string Name, AppSnapshot Snapshot)> apps)
    {
        foreach (var (name, snapshot) in apps)
        {
            var key = IntoChat.OS.Host.Primitives.AppRef.Of(brain, name).Key;
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
