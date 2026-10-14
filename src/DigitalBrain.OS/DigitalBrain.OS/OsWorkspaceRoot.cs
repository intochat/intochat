using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Framework;

namespace DigitalBrain.OS;

// Deployment/migration only. The installed Workspace program composes every visible control.
internal sealed class OsWorkspaceRoot(IGrainFactory grains, IDigitalBrain brain, AppInstaller installer, ILogger<OsWorkspaceRoot> logger) : IUiRootProvider
{
    public string ActivationApp => "workspace";

    public async Task<UiChildRef> Connect(CancellationToken cancellationToken)
    {
        var scope = BrainScope.CurrentId();
        var workspace = grains.GetGrain<IApp>(IApp.Key(scope, "workspace"));
        if ((await workspace.Read()).Manifest.Title.Length == 0)
        {
            try { await installer.Install("os", "workspace", cancellationToken); }
            catch (InvalidOperationException)
            { if ((await workspace.Read()).Manifest.Title.Length == 0) { throw; } }
        }
        // Explicitly preserve personal programs. The common updater preserves settings and connections.
        await foreach (var instance in brain.Kernel.Instances<IApp>(cancellationToken))
        {
            if (IApp.NameOf(scope, instance.Key) is not { } name) { continue; }
            var app = await grains.GetGrain<IApp>(instance.Key).Read();
            if (app.Edited || app.Origin is not { Source: AppSource.Shipped } origin || origin.Publisher is not ("os" or "intochat")) { continue; }
            try { await installer.Update(name, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException && name != "workspace")
            { logger.LogWarning(error, "App {App} could not migrate its view; its installed version was retained.", name); }
        }
        return new("layout", IApp.Key(scope, "workspace") + "/root");
    }
}
