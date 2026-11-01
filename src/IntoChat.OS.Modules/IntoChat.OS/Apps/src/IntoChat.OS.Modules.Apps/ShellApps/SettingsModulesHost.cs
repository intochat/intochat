using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Writes the loaded module list where the Settings script reads it. Connections stay on the broker.
internal sealed class SettingsModulesHost(
    IServiceProvider services, DigitalBrain.Spi.IClusterListeners listeners, ILogger<SettingsModulesHost> logger) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(SettingsModulesHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => WriteAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task WriteAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller)
            {
                CallerContextStamper.Stamp(caller);
            }
            var text = SiloNeurons.Get<IText>(services, IApp.Key(brain, "settings") + "/modules-json");
            await text.Set(InstalledModules.Json());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Settings modules were not published in {Brain}.", brain); }
    }
}

internal sealed class SettingsModulesLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<SettingsModulesLifecycle>(ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<SettingsModulesHost>(services).Execute(cancellationToken));
    }
}
