using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

// Seeds each brain's default local folder when the brain activates. The Files screen itself is a compiled screen (app.files) the
// catalog installs and the shell reads like any other; this host keeps only the folder its first open should find.
internal sealed class FilesHost(IServiceProvider services, DigitalBrain.Kernel.IClusterListeners listeners) : IStartupTask
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
        _ = Task.Run(() => EnsureAsync("owner"), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAsync(string brain)
    {
        var gate = _gates.GetOrAdd(brain, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await EnsureDefaultLocalRootsAsync(services, brain); }
        finally { gate.Release(); }
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
}

internal sealed class FilesLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(FilesHost), ServiceLifecycleStage.Active,
            ct => ActivatorUtilities.CreateInstance<FilesHost>(services).Execute(ct), _ => Task.CompletedTask);
}
