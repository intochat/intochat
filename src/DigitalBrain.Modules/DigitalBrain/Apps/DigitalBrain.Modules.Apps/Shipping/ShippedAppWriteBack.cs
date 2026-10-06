using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Development: a publish in a writable source's publisher brain writes the published version back
// into the source folder, so editing in the shell shows up as an ordinary repository diff.
internal sealed class ShippedAppWriteBack(
    LocalSignalHub signals, IGrainFactory grains, IPublisherDirectory publishers,
    IEnumerable<IShippedAppSource> sources, ILogger<ShippedAppWriteBack> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var writable = sources.Where(source => source.Writable).ToArray();
        if (writable.Length == 0) { return; }
        using var published = signals.Subscribe<AppPublished>();
        await foreach (var signal in published.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var source in writable)
            {
                try
                {
                    var publisher = await publishers.Resolve(source.Account, stoppingToken);
                    var app = grains.GetGrain<IApp>(IApp.Key(publisher.ScopeId, signal.Name));
                    if (signal.Publisher != app.GetGrainId().ToString()) { continue; }
                    await ShippedAppSync.WriteIfFastForward(source, app, await app.ExportPublished(), logger);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Writing {App} back to the {Account} folder failed.", signal.Name, source.Account); }
            }
        }
    }
}
