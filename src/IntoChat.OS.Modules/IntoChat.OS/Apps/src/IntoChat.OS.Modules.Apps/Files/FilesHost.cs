using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Hosts Files in this process. The workspace neuron still publishes the same views; the script folder no longer does.
internal sealed class FilesHost(IServiceProvider services, DigitalBrain.Spi.IClusterListeners listeners, ILogger<FilesHost> logger) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(FilesHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => EnsureAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });
        _ = await listeners.Listen<AppRequested>(nameof(FilesHost), (publisher, signal, token) =>
        {
            if (publisher.Key != "apps/files") { return Task.CompletedTask; }
            _ = OpenAsync(publisher.Brain.Value, signal, token);
            return Task.CompletedTask;
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            var app = SiloNeurons.Get<IApp>(services, IApp.Key(brain, "files"));
            if ((await app.Read()).Manifest is { Title.Length: > 0 }) { return; }
            await app.Import(FilesRequests.Bundle);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The files app was not installed in {Brain}.", brain); }
    }

    private async Task OpenAsync(string brain, AppRequested request, CancellationToken cancellationToken)
    {
        try
        {
            if (await HostCaller.ForBrain(services, brain) is not { } caller || caller.BrainId != brain) { return; }
            CallerContextStamper.Stamp(caller);
            var key = IApp.Key(brain, "files");
            var app = SiloNeurons.Get<IApp>(services, key);
            var workspace = SiloNeurons.Get<IFilesWorkspace>(services, key + "/workspace");
            if (request.Operation == "open")
            {
                await workspace.Open(cancellationToken);
                var snapshot = await app.Read();
                CallerContextStamper.Stamp(caller with { AppId = key });
                try
                {
                    await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
                }
                finally
                {
                    CallerContextStamper.Stamp(caller);
                }
            }
            if (await FilesRequests.Forward(workspace, request, cancellationToken) is { } failure)
            { await app.Progress("failed", failure); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Files did not answer {Operation} in {Brain}.", request.Operation, brain); }
    }
}

internal sealed class FilesLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<FilesLifecycle>(ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<FilesHost>(services).Execute(cancellationToken));
    }
}
