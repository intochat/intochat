using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;

namespace DigitalBrain.AI.Routing;

// Fills the operation menu from the apps already installed, then keeps one app current when it changes.
// The scan runs at startup, outside a person's request. A route after that reads the menu.
internal sealed class AppMenuHost(LocalSignalHub hub, IGrainFactory grains, IKernel kernel, AppOperationCatalog catalog, ILogger<AppMenuHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var changes = hub.Subscribe<AppChanged>();
        await foreach (var change in changes.Reader.ReadAllAsync(stoppingToken))
        {
            var key = change.Publisher.Name[(change.Publisher.Name.IndexOf('/') + 1)..];
            try { await Refresh(key, stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "The app menu could not apply {App}.", key); }
        }
    }

    // The first change in a brain reads the whole menu. Later changes read the one app.
    // Both happen as apps are installed, not when a person asks to route a message.
    private async Task Refresh(string key, CancellationToken cancellationToken)
    {
        var marker = key.IndexOf("/apps/", StringComparison.Ordinal);
        if (marker <= 0) { return; }
        var brain = key[..marker];
        if (!catalog.TryGet(brain, out _)) { await Scan(brain, cancellationToken); return; }
        if (IApp.NameOf(brain, key) is not { } name) { return; }
        catalog.Upsert(brain, name, await Operations(key, name, cancellationToken));
    }

    private async Task Scan(string brain, CancellationToken cancellationToken)
    {
        List<(string App, MenuOperation[] Operations)> apps = [];
        await foreach (var instance in kernel.InstancesIn(brain, kernel.TypeOf(typeof(IApp)), cancellationToken))
        {
            if (IApp.NameOf(brain, instance.Key) is not { } name) { continue; }
            apps.Add((name, await Operations(instance.Key, name, cancellationToken)));
        }
        catalog.Replace(brain, apps);
    }

    private async Task<MenuOperation[]> Operations(string key, string name, CancellationToken cancellationToken)
    {
        AppSnapshot snapshot;
        try { snapshot = await grains.GetGrain<IApp>(key).Read().WaitAsync(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
        if (snapshot.MigrationTarget is not null || snapshot.Manifest is null) { return []; }
        return snapshot.Manifest.Operations
            .Select(operation => new MenuOperation(name, operation.Name, string.IsNullOrWhiteSpace(operation.Description) ? null : operation.Description))
            .ToArray();
    }
}
