using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Graph;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Hosts the brain app in this process. The scene is set when someone opens it, then particles follow deliveries.
internal sealed class BrainSceneHost(
    IServiceProvider services, DigitalBrain.Spi.IClusterListeners listeners, ILogger<BrainSceneHost> logger) : IStartupTask
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pumps = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (GraphLevel Level, string? Focus)> _views = new(StringComparer.Ordinal);

    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(BrainSceneHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => EnsureAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });
        _ = await listeners.Listen<AppRequested>(nameof(BrainSceneHost), (publisher, signal, token) =>
        {
            if (publisher.Key != "apps/brain" || signal.Operation != "open") { return Task.CompletedTask; }
            _ = OpenAsync(publisher.Brain.Value, token);
            return Task.CompletedTask;
        });
        try
        {
            _ = await listeners.Listen<SceneInput>(nameof(BrainSceneHost), (publisher, signal, token) =>
            {
                if (signal.Kind != "tap" || signal.Name != IScene.Key(publisher.Brain.Value, "brain", BrainScene.SceneName))
                { return Task.CompletedTask; }
                if (!CallerContextStamper.TryGet(out var caller) || caller.BrainId != publisher.Brain.Value) { return Task.CompletedTask; }
                _ = ZoomAsync(publisher.Brain.Value, caller, signal.NodeId, token);
                return Task.CompletedTask;
            });
        }
        catch (ArgumentException error)
        { logger.LogWarning(error, "The brain scene will not zoom until Flame signals are registered."); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller)
            {
                CallerContextStamper.Stamp(caller);
            }
            var app = SiloNeurons.Get<IApp>(services, IApp.Key(brain, "brain"));
            if ((await app.Read()).Manifest is { Title.Length: > 0 }) { return; }
            await app.Import(BrainScene.Bundle);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain app was not installed in {Brain}.", brain); }
    }

    private async Task OpenAsync(string brain, CancellationToken cancellationToken)
    {
        try
        {
            if (await HostCaller.ForBrain(services, brain) is not { } caller || caller.BrainId != brain) { return; }
            CallerContextStamper.Stamp(caller);
            var view = _views.GetOrAdd(brain, _ => (GraphLevel.Brain, null));
            await SetAsync(brain, caller, await ComposeAsync(brain, view, [], 1), cancellationToken);
            StartPump(brain, caller);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain scene for {Brain} did not open.", brain); }
    }

    private void StartPump(string brain, CapabilityCaller caller)
    {
        lock (_gate) { if (!_pumps.Add(brain)) { return; } }
        _ = PumpAsync(brain, caller);
    }

    private async Task PumpAsync(string brain, CapabilityCaller caller)
    {
        try
        {
            CallerContextStamper.Stamp(caller);
            var observer = services.GetRequiredService<IBrainObserver>();
            var trail = new List<SignalDelivery>(BrainScene.MaxTrailSteps);
            await foreach (var delivery in observer.Deliveries(CancellationToken.None))
            {
                CallerContextStamper.Stamp(caller);
                trail.Insert(0, delivery);
                if (trail.Count > BrainScene.MaxTrailSteps)
                {
                    trail.RemoveRange(BrainScene.MaxTrailSteps, trail.Count - BrainScene.MaxTrailSteps);
                }
                var scene = SiloNeurons.Get<IScene>(services, IScene.Key(brain, "brain", BrainScene.SceneName));
                var zoom = (await scene.Read()).Definition.Camera.Zoom;
                var view = _views.GetOrAdd(brain, _ => (GraphLevel.Brain, null));
                await SetAsync(brain, caller, await ComposeAsync(brain, view, trail, zoom), CancellationToken.None);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain scene for {Brain} stopped following deliveries.", brain); }
    }

    private async Task ZoomAsync(string brain, CapabilityCaller caller, string? nodeId, CancellationToken cancellationToken)
    {
        try
        {
            CallerContextStamper.Stamp(caller);
            var scene = SiloNeurons.Get<IScene>(services, IScene.Key(brain, "brain", BrainScene.SceneName));
            var state = await scene.Read();
            var node = !string.IsNullOrEmpty(nodeId)
                ? state.Definition.Nodes.FirstOrDefault(n => n.Id == nodeId)
                : null;
            // A node that drills changes the level; the camera resets to the whole picture.
            if (node is not null && node.Parameters.TryGetValue(BrainSceneGraph.DrillParameter, out var drill))
            {
                var current = _views.GetOrAdd(brain, _ => (GraphLevel.Brain, null));
                var next = BrainSceneGraph.Next(drill, current.Level, current.Focus);
                _views[brain] = next;
                await SetAsync(brain, caller, await ComposeAsync(brain, next, [], 1), cancellationToken);
                return;
            }
            var nextZoom = BrainScene.NextZoom(state.Definition.Camera.Zoom);
            var camera = node is not null
                ? new SceneCamera(node.Transform.X, node.Transform.Y, nextZoom)
                : state.Definition.Camera with { Zoom = nextZoom };
            var definition = state.Definition with { Camera = camera };
            await SetAsync(brain, caller, definition, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain scene for {Brain} did not zoom.", brain); }
    }

    // The scene comes from the brain graph projection (one neuron per brain). The pump passes the live trail; a drill passes none.
    private async Task<SceneDefinition> ComposeAsync(string brain, (GraphLevel Level, string? Focus) view, IReadOnlyList<SignalDelivery> trail, double zoom)
    {
        var graph = SiloNeurons.Get<IBrainGraph>(services, brain + "/default");
        var snapshot = await graph.Snapshot(view.Level, view.Focus);
        return BrainSceneGraph.Compose(snapshot, view.Level, view.Focus, trail, zoom);
    }

    private async Task SetAsync(string brain, CapabilityCaller caller, SceneDefinition definition, CancellationToken cancellationToken)
    {
        var scene = SiloNeurons.Get<IScene>(services, IScene.Key(brain, "brain", BrainScene.SceneName));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            CallerContextStamper.Stamp(caller);
            var revision = (await scene.Read()).Revision;
            try
            {
                await scene.Set(definition, revision);
                return;
            }
            catch (RevisionConflictException error) when (attempt == 0)
            {
                logger.LogDebug(error, "The brain scene for {Brain} was written again after a revision conflict.", brain);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal sealed class BrainSceneLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<BrainSceneLifecycle>(ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<BrainSceneHost>(services).Execute(cancellationToken));
    }
}
