using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS.Runtime;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel;

// Deployment/migration only. The installed Workspace program composes every visible control.
internal sealed class OsWorkspaceRoot(INeuronProxies brain, IBrainObserver kernel, AppInstaller installer, ILogger<OsWorkspaceRoot> logger,
    IEnumerable<IFeatureMigration> migrations, IOptions<OsAppsOptions> options) : IUiRootProvider
{
    public string ActivationApp => "workspace";

    private T Neuron<T>(string relative) where T : class => brain.Get<T>($"{BrainScope.CurrentId()}/{relative}");

    public async Task<UiChildRef> Connect(CancellationToken cancellationToken)
    {
        var scope = BrainScope.CurrentId();
        foreach (var migration in migrations)
        {
            try { await migration.PrepareBrain(scope, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "An app migration is incomplete in {Brain}; its saved state is retained.", scope); }
        }
        foreach (var required in new OsApp[] { new("os", "usage"), new("os", "apps"), new("os", "circle"), new("os", "workspace"), new("os", "desktop"), new("os", "filmcraft"), new("os", "photocraft") })
        {
            var app = Neuron<IApp>($"apps/{required.Name}");
            if ((await app.Read()).Manifest.Title.Length != 0) { continue; }
            // The OS's required apps ship asking for nothing, so they install with the consent of no requirements;
            // one that came to ask for more is refused here rather than installed without the person.
            try
            {
                var outcome = await installer.Install(new(new(required.Publisher, required.Name), AppConsent.Of(null), Guid.Empty), cancellationToken);
                if (outcome.Status != InstallStatus.Ok)
                { throw new InvalidOperationException($"Required app {required.Name} did not install: {outcome.Message}"); }
            }
            catch (InvalidOperationException)
            { if ((await app.Read()).Manifest.Title.Length == 0) { throw; } }
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
        // v2 for everyone unless the person chose the classic layout; classic is v1, unchanged.
        // The choice is the desktop's own text (layout-switch.cs writes it), read fresh on every connect.
        // Only the desktop the OS ships is the v2 root: an app that merely bears the name (own, foreign
        // or edited) would otherwise gain the desktop's workspace authority, so v1 is shown instead.
        var desktop = Neuron<IApp>("apps/desktop");
        var shipped = ShippedApps.Is(await desktop.Read(), "desktop", options.Value.TrustedPublishers);
        if (!shipped)
        { logger.LogWarning("The app named desktop in {Brain} is not the shipped desktop; the classic layout is shown.", scope); }
        if (!shipped || (await Neuron<IText>("apps/desktop/layout").Read()).Markdown == "classic")
        {
            await Neuron<IApp>("apps/workspace").Request("open", "");
            return new("layout", IApp.Key(scope, "workspace") + "/root");
        }
        await desktop.Request("start", "");
        return new(UIVocabulary.ViewType, IApp.Key(scope, "desktop") + "/view");
    }
}
