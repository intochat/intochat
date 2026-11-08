using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Graph;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<AppNavigated>> _navigations = new(StringComparer.Ordinal);

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (services.GetService<InProcessAppHost>() is { } inProcessHost)
        {
            inProcessHost.Navigated += OnNavigated;
        }
        try
        {
            _ = await listeners.Listen<AppNavigated>(nameof(BrainSceneHost), (publisher, signal, _) =>
            {
                OnNavigated(signal with { App = string.IsNullOrWhiteSpace(signal.App) ? publisher.ToString() : signal.App });
                return Task.CompletedTask;
            });
        }
        catch (ArgumentException error)
        { logger.LogDebug(error, "AppNavigated listener will attach when registered."); }

        _ = await listeners.Listen<Activated>(nameof(BrainSceneHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => EnsureAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });
        _ = await listeners.Listen<AppRequested>(nameof(BrainSceneHost), (publisher, signal, token) =>
        {
            if (publisher.Key != "apps/brain") { return Task.CompletedTask; }
            var brain = publisher.Brain.Value;
            signal = signal with { Input = CompiledAppsHost.TextOf(signal.Input) };
            if (signal.Operation == "work")
            {
                _views[brain] = (GraphLevel.Cluster, "tasks");
                _ = OpenAsync(brain, token);
                return Task.CompletedTask;
            }
            if (signal.Operation == "drill" && !string.IsNullOrWhiteSpace(signal.Input))
            {
                var current = _views.GetOrAdd(brain, _ => (GraphLevel.Brain, null));
                _views[brain] = BrainSceneGraph.Next(signal.Input.Trim(), current.Level, current.Focus);
                _ = OpenAsync(brain, token);
                return Task.CompletedTask;
            }
            if (signal.Operation == "focus" && !string.IsNullOrWhiteSpace(signal.Input))
            {
                _views[brain] = (GraphLevel.Neurons, ResolveFocusNode(brain, signal.Input.Trim()));
                _ = OpenAsync(brain, token);
                return Task.CompletedTask;
            }
            if (signal.Operation == "open")
            {
                var focus = ExtractFocus(signal.Input);
                if (string.Equals(focus, "tasks", StringComparison.OrdinalIgnoreCase))
                {
                    _views[brain] = (GraphLevel.Cluster, "tasks");
                }
                else if (!string.IsNullOrWhiteSpace(focus))
                {
                    _views[brain] = (GraphLevel.Neurons, ResolveFocusNode(brain, focus));
                }
                else
                {
                    _views[brain] = (GraphLevel.Brain, null);
                }
                _ = OpenAsync(brain, token);
            }
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
        _ = Task.Run(() => EnsureAsync("owner"), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            var caller = await HostCaller.ForBrain(services, brain);
            if (caller is not null)
            {
                CallerContextStamper.Stamp(caller);
            }
            var appKey = IApp.Key(brain, "brain");
            var app = SiloNeurons.Get<IApp>(services, appKey);
            var snapshot = await app.Read();
            if (snapshot.Manifest is not { Title.Length: > 0 })
            {
                await app.Import(BrainScene.Bundle);
                snapshot = await app.Read();
            }
            if (snapshot.Presentation is null && caller is not null)
            {
                using var appScope = CallerContextStamper.StampScope(HostCaller.AsApp(caller, appKey));
                var viewRef = new UiChildRef(UIVocabulary.ViewType, appKey + "/view");
                await app.Present(
                    Math.Max(1L, snapshot.ProgramRevision),
                    new Presentation(
                        new Sphere("hub", []),
                        Main: viewRef,
                        Views: [new PresentationView("full", viewRef)]));
            }
            if (caller is not null && caller.BrainId == brain)
            {
                var viewNeuron = SiloNeurons.Get<IView>(services, appKey + "/view");
                var currentView = await viewNeuron.Read();
                if (currentView is null)
                {
                    var view = _views.GetOrAdd(brain, _ => (GraphLevel.Brain, null));
                    var definition = await ComposeAsync(brain, view, [], 1);
                    await PublishUiViewAsync(brain, caller, view, definition);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain app was not initialized in {Brain}.", brain); }
    }

    private async Task OpenAsync(string brain, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureAsync(brain);
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
        if (view.Level == GraphLevel.Neurons && !string.IsNullOrWhiteSpace(view.Focus) && snapshot.IndexOf(view.Focus) < 0)
        {
            await graph.Rebuild();
            snapshot = await graph.Snapshot(view.Level, view.Focus);
            if (snapshot.IndexOf(view.Focus) < 0 && view.Focus.EndsWith("/apps/datavisualizer", StringComparison.OrdinalIgnoreCase))
            {
                var alt = IApp.Key(brain, "data");
                var altSnap = await graph.Snapshot(view.Level, alt);
                if (altSnap.IndexOf(alt) >= 0)
                {
                    view = (view.Level, alt);
                    _views[brain] = view;
                    snapshot = altSnap;
                }
            }
        }
        IReadOnlyList<AppNavigated> navigations = [];
        if (_navigations.TryGetValue(brain, out var list))
        {
            lock (_gate)
            {
                navigations = [.. list];
            }
        }
        return BrainSceneGraph.Compose(snapshot, view.Level, view.Focus, trail, zoom, navigations);
    }

    private void OnNavigated(AppNavigated navigated)
    {
        var brain = BrainKey.BrainOf(navigated.App);
        if (string.IsNullOrWhiteSpace(brain)) { brain = "local"; }
        var list = _navigations.GetOrAdd(brain, _ => new List<AppNavigated>(BrainScene.MaxTrailSteps));
        lock (_gate)
        {
            list.Insert(0, navigated);
            if (list.Count > BrainScene.MaxTrailSteps)
            {
                list.RemoveRange(BrainScene.MaxTrailSteps, list.Count - BrainScene.MaxTrailSteps);
            }
        }
    }

    internal static string? ExtractFocus(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return null; }
        var trimmed = input.Trim();
        if (trimmed == "{}") { return null; }
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("focus", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }
                if (doc.RootElement.TryGetProperty("Focus", out var prop2) && prop2.ValueKind == JsonValueKind.String)
                {
                    return prop2.GetString();
                }
            }
            catch (JsonException)
            {
                return null;
            }
            return null;
        }
        return trimmed.Trim('"');
    }

    internal static string ResolveFocusNode(string brain, string focus)
    {
        if (focus.Contains('/', StringComparison.Ordinal)) { return focus; }
        return IApp.Key(brain, focus);
    }

    private async Task PublishUiViewAsync(string brain, CapabilityCaller caller, (GraphLevel Level, string? Focus) view, SceneDefinition definition)
    {
        CallerContextStamper.Stamp(caller);
        var uiScene = definition;
        if (view.Level == GraphLevel.Brain && string.IsNullOrWhiteSpace(view.Focus) && definition.Nodes.Count(n => n.Type == "sphere") < 3)
        {
            uiScene = await ComposeAsync(brain, (GraphLevel.Neurons, null), [], definition.Camera.Zoom);
        }
        var uiRoot = BrainSceneGraph.BuildUiRoot(uiScene, view.Level, view.Focus);
        var viewNeuron = SiloNeurons.Get<IView>(services, IApp.Key(brain, "brain") + "/view");
        await viewNeuron.Publish(1, uiRoot);
    }

    private async Task SetAsync(string brain, CapabilityCaller caller, SceneDefinition definition, CancellationToken cancellationToken)
    {
        var view = _views.GetOrAdd(brain, _ => (GraphLevel.Brain, null));
        await PublishUiViewAsync(brain, caller, view, definition);
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
