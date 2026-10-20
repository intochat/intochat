using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel;

// Deployment/migration only. The installed Workspace program composes every visible control.
internal sealed class OsWorkspaceRoot(IIntoChatOS brain, AppInstaller installer, ILogger<OsWorkspaceRoot> logger,
    IEnumerable<IFeatureMigration> migrations, IOptions<OsAppsOptions> options) : IUiRootProvider
{
    public string ActivationApp => "workspace";

    public async Task<UiChildRef> Connect(CancellationToken cancellationToken)
    {
        var scope = BrainScope.CurrentId();
        foreach (var migration in migrations)
        {
            try { await migration.PrepareBrain(scope, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "An app migration is incomplete in {Brain}; its saved state is retained.", scope); }
        }
        foreach (var required in new OsApp[] { new("os", "usage"), new("os", "circle"), new("os", "workspace"), new("os", "desktop"), new("os", "filmcraft"), new("os", "photocraft") })
        {
            var app = brain.Neuron<IApp>($"apps/{required.Name}");
            if ((await app.Read()).Manifest.Title.Length != 0) { continue; }
            try { await installer.Install(required.Publisher, required.Name, cancellationToken); }
            catch (InvalidOperationException)
            { if ((await app.Read()).Manifest.Title.Length == 0) { throw; } }
        }
        // Explicitly preserve personal programs. The common updater preserves settings and connections.
        await foreach (var instance in brain.Kernel.Instances<IApp>(cancellationToken))
        {
            if (IApp.NameOf(scope, instance.Key) is not { } name) { continue; }
            var app = await brain.Neuron<IApp>($"apps/{name}").Read();
            if (app.Edited || app.Origin is not { Source: AppSource.Shipped } origin || !options.Value.TrustedPublishers.Contains(origin.Publisher)) { continue; }
            try { await installer.Update(name, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException && name is not ("workspace" or "desktop"))
            { logger.LogWarning(error, "App {App} could not migrate its view; its installed version was retained.", name); }
        }
        // v2 for everyone unless the person chose the classic layout; classic is v1, unchanged.
        var desktop = brain.Neuron<IApp>("apps/desktop");
        if ((await desktop.Read()).Settings.GetValueOrDefault("Layout") == "classic")
        {
            await brain.Neuron<IApp>("apps/workspace").Request("open", "");
            return new("layout", IApp.Key(scope, "workspace") + "/root");
        }
        await desktop.Request("start", "");
        return new(UIVocabulary.ViewType, IApp.Key(scope, "desktop") + "/view");
    }
}
