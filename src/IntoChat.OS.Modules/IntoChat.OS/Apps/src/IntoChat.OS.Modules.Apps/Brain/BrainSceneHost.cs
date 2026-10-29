using DigitalBrain.Capabilities;
using DigitalBrain;
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
                _ = ZoomAsync(publisher.Brain.Value, caller, token);
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
            var observer = services.GetRequiredService<IBrainObserver>();
            var neurons = new List<BrainInstance>();
            await foreach (var instance in observer.Instances(null, cancellationToken)) { neurons.Add(instance); }
            var synapses = await observer.Synapses(cancellationToken);
            await SetAsync(brain, caller, BrainScene.Compose(neurons, synapses, null, 1), cancellationToken);
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
            await foreach (var delivery in observer.Deliveries(CancellationToken.None))
            {
                CallerContextStamper.Stamp(caller);
                var neurons = new List<BrainInstance>();
                await foreach (var instance in observer.Instances(null, CancellationToken.None)) { neurons.Add(instance); }
                var synapses = await observer.Synapses(CancellationToken.None);
                var scene = SiloNeurons.Get<IScene>(services, IScene.Key(brain, "brain", BrainScene.SceneName));
                var zoom = (await scene.Read()).Definition.Camera.Zoom;
                await SetAsync(brain, caller, BrainScene.Compose(neurons, synapses, delivery, zoom), CancellationToken.None);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain scene for {Brain} stopped following deliveries.", brain); }
    }

    private async Task ZoomAsync(string brain, CapabilityCaller caller, CancellationToken cancellationToken)
    {
        try
        {
            CallerContextStamper.Stamp(caller);
            var scene = SiloNeurons.Get<IScene>(services, IScene.Key(brain, "brain", BrainScene.SceneName));
            var state = await scene.Read();
            var definition = state.Definition with { Camera = state.Definition.Camera with { Zoom = BrainScene.NextZoom(state.Definition.Camera.Zoom) } };
            await SetAsync(brain, caller, definition, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The brain scene for {Brain} did not zoom.", brain); }
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
