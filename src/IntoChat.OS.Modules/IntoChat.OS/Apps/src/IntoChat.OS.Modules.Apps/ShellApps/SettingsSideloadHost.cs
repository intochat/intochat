using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Apps;

// Allows the apps directory on each brain's local disk, and answers the three Settings sideload
// operations. It does not handle open or read; the Settings script still does.
internal sealed class SettingsSideloadHost(
    IServiceProvider services, SideloadInstaller installer, DigitalBrain.Spi.IClusterListeners listeners, ILogger<SettingsSideloadHost> logger) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(SettingsSideloadHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => AllowAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });
        _ = await listeners.Listen<AppRequested>(nameof(SettingsSideloadHost), (publisher, signal, token) =>
        {
            if (publisher.Key != "apps/settings" || signal.Operation is not ("sideload-list" or "sideload-install" or "sideload-add-git"))
            { return Task.CompletedTask; }
            _ = AnswerAsync(publisher.Brain.Value, signal, token);
            return Task.CompletedTask;
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task AllowAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        var path = SideloadPath();
        if (path is null) { return; }
        try
        {
            if (await HostCaller.ForBrain(services, brain) is not { } caller) { return; }
            CallerContextStamper.Stamp(caller);
            var disk = SiloNeurons.Get<ILocalDisk>(services, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "local-disk", "main"));
            await AppsDirectoryRoot.Allow(disk, path);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The apps directory was not added to Files in {Brain}.", brain); }
    }

    private async Task AnswerAsync(string brain, AppRequested request, CancellationToken cancellationToken)
    {
        try
        {
            if (await HostCaller.ForBrain(services, brain) is not { } caller || caller.BrainId != brain) { return; }
            CallerContextStamper.Stamp(caller);
            var message = request.Operation switch
            {
                "sideload-list" => string.Join(", ", (await installer.List(cancellationToken)).Select(row => row.Name)),
                "sideload-install" => (await installer.Install(request.Input, cancellationToken)).Message,
                "sideload-add-git" => (await installer.AddGit(request.Input, cancellationToken)).Message,
                _ => "",
            };
            if (message.Length == 0 && request.Operation == "sideload-list") { message = "No sideloaded apps."; }
            await SiloNeurons.Get<IApp>(services, IApp.Key(brain, "settings")).Progress(request.Operation, message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Settings did not answer {Operation} in {Brain}.", request.Operation, brain); }
    }

    private string? SideloadPath()
    {
        var configuration = services.GetService<MicrosoftSdk.Extensions.Configuration.IConfiguration>();
        var configured = configuration?["IntoChatOS:Apps:SideloadDirectory"];
        if (!string.IsNullOrWhiteSpace(configured)) { return System.IO.Path.GetFullPath(configured); }
        try { return DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration, DigitalBrain.Runtime.Orleans.RuntimeOptions.IntoChatCompatible).Apps; }
        catch (Exception error) when (error is InvalidOperationException or IOException) { return null; }
    }
}

internal sealed class SettingsSideloadLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<SettingsSideloadLifecycle>(ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<SettingsSideloadHost>(services).Execute(cancellationToken));
    }
}
