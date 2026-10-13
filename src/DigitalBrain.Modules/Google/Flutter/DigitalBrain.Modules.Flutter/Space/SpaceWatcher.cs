using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Workspace.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Flutter.Space;

internal sealed class SpaceWatcher(LocalSignalHub hub, IGrainFactory grains, ILogger<SpaceWatcher> logger) : BackgroundService
{
    private readonly LocalSignalHub.Subscription<WorkspaceChanged> _workspace = hub.Subscribe<WorkspaceChanged>();
    private readonly LocalSignalHub.Subscription<PresentationChanged> _presentation = hub.Subscribe<PresentationChanged>();
    private readonly LocalSignalHub.Subscription<AppActivity> _activity = hub.Subscribe<AppActivity>();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Consume(_workspace, change => Rebuild(change.WorkspaceId), stoppingToken),
        Consume(_presentation, change => Rebuild(BrainOf(change.App)), stoppingToken),
        Consume(_activity, async activity =>
        {
            var publisher = activity.Publisher.Name;
            var key = publisher[(publisher.IndexOf('/') + 1)..];
            var brain = BrainOf(key);
            if (brain is null) { return; }
            var app = grains.GetGrain<IApp>(key);
            await app.Stamp("running", activity.State is "working" or "reading" or "searching" or "listening" ? "true" : "false");
            await app.Stamp("attention", activity.State == "attention" ? "true" : "false");
            await Rebuild(brain);
        }, stoppingToken));

    private async Task Consume<T>(LocalSignalHub.Subscription<T> subscription, Func<T, Task> handle, CancellationToken ct) where T : Signal
    {
        using (subscription)
        {
            await foreach (var signal in subscription.Reader.ReadAllAsync(ct))
            {
                try { await handle(signal); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Could not rebuild a space after {Signal}.", typeof(T).Name); }
            }
        }
    }

    private Task Rebuild(string? brain) => brain is null ? Task.CompletedTask : grains.GetGrain<ISpace>(brain).Rebuild();
    private static string? BrainOf(string key)
    {
        var marker = key.IndexOf("/apps/", StringComparison.Ordinal);
        return marker > 0 ? key[..marker] : null;
    }
}
