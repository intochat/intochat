using System.Collections.Concurrent;
using DigitalBrain.Log;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Hosts Files in this process. The workspace neuron still publishes the same views; the script folder no longer does.
internal sealed class FilesHost(IServiceProvider services, DigitalBrain.Kernel.IClusterListeners listeners, ILogger<FilesHost> logger) : IStartupTask
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

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
            var gate = _gates.GetOrAdd(brain, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                using var kernel = InProcessCallerScope.EnterKernel(brain);
                await EnsureDefaultLocalRootsAsync(services, brain);
                var filesApp = IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), "files");
                var key = filesApp.Key;
                var app = SiloNeurons.Get<IApp>(services, key);
                var snapshot = await app.Read();
                if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
                {
                    try
                    {
                        await app.Import(FilesRequests.Bundle);
                    }
                    catch (RuleViolationException error)
                    {
                        snapshot = await app.Read();
                        if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title)) { throw; }
                        logger.LogDebug(error, "The files app was already imported concurrently in {Brain}.", brain);
                    }
                    snapshot = await app.Read();
                }
                var workspace = SiloNeurons.Get<IFilesWorkspace>(services, filesApp.ChildKey("workspace"));
                await workspace.Open(CancellationToken.None);
                snapshot = await app.Read();
                using (InProcessCallerScope.EnterApp(brain, key))
                {
                    await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "The files app was not installed in {Brain}.", brain); }
    }

    internal static async Task EnsureDefaultLocalRootsAsync(IServiceProvider services, string brain)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<FilesHost>() ?? OsConsoleLog.For<FilesHost>();
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
            var filesApp = IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), "files");
            var key = filesApp.Key;
            var app = SiloNeurons.Get<IApp>(services, key);
            var workspace = SiloNeurons.Get<IFilesWorkspace>(services, filesApp.ChildKey("workspace"));
            if (request.Operation == "open")
            {
                await workspace.Open(cancellationToken);
                var snapshot = await app.Read();
                using (InProcessCallerScope.EnterApp(brain, key))
                {
                    await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
                }
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
