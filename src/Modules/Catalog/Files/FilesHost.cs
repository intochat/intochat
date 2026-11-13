using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DigitalBrain.Log;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Hosts Files in this process. The workspace neuron still publishes the same views; the script folder no longer does.
internal sealed class FilesHost(IServiceProvider services, DigitalBrain.Kernel.IClusterListeners listeners, ILogger<FilesHost> logger) : IStartupTask
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
        _ = Task.Run(() => EnsureAsync("owner"), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAsync(string brain)
    {
        using var cleared = InProcessCallerScope.ClearCaller();
        try
        {
            using var kernel = InProcessCallerScope.EnterKernel(brain);
            await EnsureDefaultLocalRootsAsync(services, brain);
            var key = IApp.Key(brain, "files");
            var app = SiloNeurons.Get<IApp>(services, key);
            var snapshot = await app.Read();
            if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
            {
                await app.Import(FilesRequests.Bundle);
                snapshot = await app.Read();
            }
            var workspace = SiloNeurons.Get<IFilesWorkspace>(services, key + "/workspace");
            await workspace.Open(CancellationToken.None);
            snapshot = await app.Read();
            await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The files app was not installed in {Brain}.", brain); }
    }

    internal static async Task EnsureDefaultLocalRootsAsync(IServiceProvider services, string brain)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<FilesHost>() ?? (ILogger)NullLogger.Instance;
        try
        {
            var disk = SiloNeurons.Get<ILocalDisk>(services, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "local-disk", "main"));
            var workspaceRoot = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
            {
                try { await disk.AllowRoot(workspaceRoot); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogSwallowed(error, "Failed to allow default workspace root on local disk.");
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, "Failed to resolve local-disk neuron for default local roots.");
        }
    }

    private async Task OpenAsync(string brain, AppRequested request, CancellationToken cancellationToken)
    {
        using var cleared = InProcessCallerScope.ClearCaller();
        try
        {
            using var kernel = InProcessCallerScope.EnterKernel(brain);
            await EnsureDefaultLocalRootsAsync(services, brain);
            var key = IApp.Key(brain, "files");
            var app = SiloNeurons.Get<IApp>(services, key);
            var workspace = SiloNeurons.Get<IFilesWorkspace>(services, key + "/workspace");
            if (request.Operation == "open")
            {
                await workspace.Open(cancellationToken);
                var snapshot = await app.Read();
                await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
            }
            if (await FilesRequests.Forward(workspace, request, cancellationToken) is { } failure)
            { await app.Progress("attention", failure); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Files did not answer {Operation} in {Brain}.", request.Operation, brain); }
    }
}

internal sealed class FilesLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(FilesHost), ServiceLifecycleStage.Active,
            ct => ActivatorUtilities.CreateInstance<FilesHost>(services).Execute(ct), _ => Task.CompletedTask);
}
