using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel;

// Deployment/migration only. The installed Workspace program composes every visible control.
internal sealed class OsWorkspaceRoot(INeuronProxies brain, IBrainObserver kernel, AppInstaller installer, ILogger<OsWorkspaceRoot> logger,
    IEnumerable<IFeatureMigration> migrations, IOptions<OsAppsOptions> options, FeatureCatalog catalog, FeatureLoader loader,
    ICapabilityBroker capabilities) : IUiRootProvider
{
    public string ActivationApp => "workspace";

    private T Neuron<T>(string relative) where T : class => brain.Get<T>($"{BrainScope.CurrentId()}/{relative}");

    public async Task<ViewRef> Connect(CancellationToken cancellationToken)
    {
        var scope = BrainScope.CurrentId();
        foreach (var migration in migrations)
        {
            try { await migration.PrepareBrain(scope, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "An app migration is incomplete in {Brain}; its saved state is retained.", scope); }
        }
        // The same set a new brain is born with: the shipped catalog plus ShippedApps.Roots. A circle keeps only its room, deployed again
        // when missing (a circle born before the room shipped); the same files deploy once.
        if (await (await capabilities.Identity(cancellationToken)).KindOf(scope, cancellationToken) == BrainKind.Circle)
        { await OsDefaults.DeployCircleUnits(brain, logger, scope); }
        else
        {
            await ShippedInstall.Missing(catalog, loader, scope, async name => await Neuron<IApp>($"apps/{name}").Read(),
                (error, app) => logger.LogWarning(error, "Default app {App} did not install in {Brain}.", app, scope), cancellationToken);
        }
        // Explicitly preserve personal programs. The common updater preserves settings and connections.
        await foreach (var instance in kernel.Instances<IApp>(cancellationToken))
        {
            if (IApp.NameOf(scope, instance.Key) is not { } name) { continue; }
            var app = await Neuron<IApp>($"apps/{name}").Read();
            if (app.Edited || app.Origin is not { Source: AppSource.Shipped } origin || !options.Value.TrustedPublishers.Contains(origin.Publisher)) { continue; }
            // Never widens: an update that asks for more waits, at the installed version, for the person's consent in Apps.
            try
            {
                var outcome = await installer.Update(new(name), cancellationToken);
                if (outcome.Status != InstallStatus.Ok)
                { logger.LogInformation("App {App} has an update that asks for more; its installed version was retained until the person consents.", name); }
            }
            catch (Exception error) when (error is not OperationCanceledException && name is not ("workspace" or "desktop"))
            { logger.LogWarning(error, "App {App} could not migrate its view; its installed version was retained.", name); }
        }
        // The desktop's view is the root. Only the desktop the OS ships is: an app that merely bears the name (own, foreign or
        // edited) would otherwise gain the desktop's workspace authority, so the workspace app's view is shown instead.
        var desktop = Neuron<IApp>("apps/desktop");
        if (!ShippedApps.Is(await desktop.Read(), "desktop", options.Value.TrustedPublishers))
        {
            logger.LogWarning("The app named desktop in {Brain} is not the shipped desktop; the workspace view is shown.", scope);
            await Neuron<IApp>("apps/workspace").Request("open", "");
            return new(UIVocabulary.ViewType, IApp.Key(scope, "workspace") + "/view");
        }
        await desktop.Request("start", "");
        return new(UIVocabulary.ViewType, IApp.Key(scope, "desktop") + "/view");
    }
}
