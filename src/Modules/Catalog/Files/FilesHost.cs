using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Log;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using IntoChat.OS.Host.Logging;

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
        var edge = services.GetService<EdgeAuthority>();
        try
        {
            var gate = _gates.GetOrAdd(brain, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var brainId = new BrainId(brain);
                using var module = edge?.Enter(edge.Module("apps.files", brainId));
                await EnsureDefaultLocalRootsAsync(services, brain);
                var filesApp = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "files");
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
                await RunAsFilesAppAsync(edge, key, brainId, async () =>
                {
                    await workspace.Open(CancellationToken.None);
                    snapshot = await app.Read();
                    await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
                });
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
        // The files module of this brain seeds the default folder, whoever's request led here (the local disk takes root
        // changes only from the person of the brain or the host).
        var edge = services.GetService<EdgeAuthority>();
        using var module = edge?.Enter(edge.Module("apps.files", new BrainId(brain)));
        try
        {
            var disk = SiloNeurons.Get<ILocalDisk>(services, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "local-disk", "main"));
            // The owner's Documents folder, not the process's working directory (2026-11-17 design, decision 3).
            var workspaceRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
        var edge = services.GetService<EdgeAuthority>();
        try
        {
            var brainId = new BrainId(brain);
            using var module = edge?.Enter(edge.Module("apps.files", brainId));
            await EnsureDefaultLocalRootsAsync(services, brain);
            var filesApp = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "files");
            var key = filesApp.Key;
            var app = SiloNeurons.Get<IApp>(services, key);
            var workspace = SiloNeurons.Get<IFilesWorkspace>(services, filesApp.ChildKey("workspace"));
            await RunAsFilesAppAsync(edge, key, brainId, async () =>
            {
                if (request.Operation == "open")
                {
                    await workspace.Open(cancellationToken);
                    var snapshot = await app.Read();
                    await app.Present(snapshot.ProgramRevision, FilesRequests.Presentation(key));
                }
                if (await FilesRequests.Forward(workspace, request, cancellationToken) is { } failure)
                { await app.Progress("attention", failure); }
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Files did not answer {Operation} in {Brain}.", request.Operation, brain); }
    }

    private static async Task RunAsFilesAppAsync(EdgeAuthority? edge, string appKey, BrainId brainId, Func<Task> action)
    {
        using (edge?.EnterInProcess())
        using (edge?.Enter(edge.App(appKey, new Actor.Person(AppPrincipal.Id(appKey), brainId.Value), brainId)))
        {
            await action();
        }
    }
}

internal sealed class FilesLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(FilesHost), ServiceLifecycleStage.Active,
            ct => ActivatorUtilities.CreateInstance<FilesHost>(services).Execute(ct), _ => Task.CompletedTask);
}
