using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.Windows;

internal sealed class WindowsFolderWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IServiceProvider? _services;
    private readonly Func<string, FileChanged, CancellationToken, Task>? _publisher;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly ILogger<WindowsFolderWatcher>? _logger;
    private readonly ConcurrentDictionary<string, WatchEntry> _watches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private bool _disposed;

    [ActivatorUtilitiesConstructor]
    public WindowsFolderWatcher(
        IServiceProvider? services = null,
        TimeProvider? clock = null,
        ILogger<WindowsFolderWatcher>? logger = null)
        : this(services, publisher: null, clock, debounce: null, logger)
    {
    }

    internal WindowsFolderWatcher(
        IServiceProvider? services,
        Func<string, FileChanged, CancellationToken, Task>? publisher,
        TimeProvider? clock = null,
        TimeSpan? debounce = null,
        ILogger<WindowsFolderWatcher>? logger = null)
    {
        _services = services;
        _publisher = publisher;
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLoopStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? flushTask;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            cts = _cts;
            flushTask = _flushTask;
            _cts = null;
            _flushTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (flushTask is not null)
        {
            try
            {
                await flushTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        DisposeAllWatches();
        _pending.Clear();
    }

    public void SyncBrain(string brainId, IReadOnlyList<WindowsFolderGrant> grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(grants);

        if (_disposed)
        {
            return;
        }

        var desired = new Dictionary<string, (WindowsFolderGrant Grant, string NormalizedRoot)>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (!grant.Enabled || string.IsNullOrWhiteSpace(grant.HostPath) || string.IsNullOrWhiteSpace(grant.Slot))
            {
                continue;
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = PathConfinement.Normalize(grant.HostPath);
            }
            catch (ArgumentException error)
            {
                _logger?.LogWarning(error, "Skipping watch for invalid host path '{HostPath}' on slot '{Slot}'.", grant.HostPath, grant.Slot);
                continue;
            }

            if (!Directory.Exists(normalizedRoot))
            {
                _logger?.LogDebug("Skipping watch for missing host folder '{HostPath}' on slot '{Slot}'.", normalizedRoot, grant.Slot);
                continue;
            }

            var key = WatchKey(brainId, grant.AppKey, grant.Slot);
            desired[key] = (grant, normalizedRoot);
        }

        foreach (var (key, existing) in _watches.ToArray())
        {
            if (!string.Equals(existing.BrainId, brainId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!desired.TryGetValue(key, out var target) ||
                !string.Equals(existing.NormalizedRoot, target.NormalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (_watches.TryRemove(key, out var removed))
                {
                    removed.Watcher.Dispose();
                }
            }
        }

        foreach (var (key, (grant, normalizedRoot)) in desired)
        {
            if (_watches.TryGetValue(key, out var existing) &&
                string.Equals(existing.NormalizedRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entry = CreateWatchEntry(brainId, grant.AppKey, grant.Slot, normalizedRoot);
            if (!_watches.TryAdd(key, entry))
            {
                entry.Watcher.Dispose();
            }
        }

        if (desired.Count > 0)
        {
            EnsureLoopStarted();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            cts = _cts;
            _cts = null;
            _flushTask = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        DisposeAllWatches();
        _pending.Clear();
    }

    private WatchEntry CreateWatchEntry(string brainId, string appKey, string slot, string normalizedRoot)
    {
        var watcher = new FileSystemWatcher(normalizedRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        var entry = new WatchEntry(brainId, appKey, slot, normalizedRoot, watcher);
        watcher.Created += (_, args) => Enqueue(entry, args.FullPath, "Created");
        watcher.Changed += (_, args) => Enqueue(entry, args.FullPath, "Changed");
        watcher.Deleted += (_, args) => Enqueue(entry, args.FullPath, "Deleted");
        watcher.Renamed += (_, args) => Enqueue(entry, args.FullPath, "Renamed");
        watcher.Error += (_, args) =>
        {
            var error = args.GetException();
            _logger?.LogWarning(error, "FileSystemWatcher error on '{Root}' (slot '{Slot}').", normalizedRoot, slot);
        };
        watcher.EnableRaisingEvents = true;
        return entry;
    }

    private void Enqueue(WatchEntry entry, string fullPath, string kind)
    {
        if (_disposed || string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        if (!string.Equals(kind, "Deleted", StringComparison.Ordinal) && Directory.Exists(fullPath))
        {
            return;
        }

        string confinedPath;
        try
        {
            confinedPath = PathConfinement.Resolve([entry.NormalizedRoot], fullPath);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            _logger?.LogDebug(error, "Ignoring confined or transient path event '{FullPath}' on '{Root}'.", fullPath, entry.NormalizedRoot);
            return;
        }

        var relativePath = Path.GetRelativePath(entry.NormalizedRoot, confinedPath).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath == "." ||
            relativePath == ".." ||
            relativePath.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var pendingKey = $"{entry.BrainId}\0{entry.Slot}\0{relativePath}";
        _pending.AddOrUpdate(
            pendingKey,
            _ => new PendingChange(entry.BrainId, entry.Slot, confinedPath, relativePath, kind, now),
            (_, existing) =>
            {
                var mergedKind = (existing.Kind, kind) switch
                {
                    ("Created", "Changed") => "Created",
                    _ => kind,
                };
                return existing with { FullPath = confinedPath, Kind = mergedKind, LastSeenUtc = now };
            });
    }

    private void EnsureLoopStarted()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _flushTask is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _flushTask = Task.Run(() => RunFlushLoopAsync(token), token);
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        var tickMs = Math.Clamp((long)(_debounce.TotalMilliseconds / 4), 10, 50);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickMs), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = _clock.GetUtcNow();
                foreach (var (key, item) in _pending.ToArray())
                {
                    if (now - item.LastSeenUtc < _debounce ||
                        !_pending.TryRemove(new KeyValuePair<string, PendingChange>(key, item)))
                    {
                        continue;
                    }

                    var signal = new FileChanged(
                        item.Slot,
                        item.Kind,
                        item.RelativePath,
                        ReadSizeSafely(item),
                        now);

                    try
                    {
                        await PublishAsync(item.BrainId, signal, cancellationToken).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Watcher background loop must survive a transient brain publish failure
                    catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        _logger?.LogWarning(error, "Failed to publish FileChanged for brain '{BrainId}', slot '{Slot}', path '{Path}'.", item.BrainId, item.Slot, item.RelativePath);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishAsync(string brainId, FileChanged signal, CancellationToken cancellationToken)
    {
        if (_publisher is not null)
        {
            await _publisher(brainId, signal, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_services is null)
        {
            return;
        }

        var edge = _services.GetService<EdgeAuthority>();
        using var edgeScope = edge?.EnterKernel(new BrainId(brainId));
        var gateway = SiloNeurons.Get<IWindowsFolderSignals>(_services, IWindowsFolderGateway.Key(brainId));
        await gateway.PublishFileChangedAsync(signal, cancellationToken).ConfigureAwait(false);
    }

    private long ReadSizeSafely(PendingChange item)
    {
        if (string.Equals(item.Kind, "Deleted", StringComparison.Ordinal))
        {
            return 0L;
        }

        try
        {
            var info = new FileInfo(item.FullPath);
            return info.Exists ? info.Length : 0L;
        }
        catch (IOException error)
        {
            _logger?.LogDebug(error, "Could not read file size for '{Path}'.", item.FullPath);
            return 0L;
        }
        catch (UnauthorizedAccessException error)
        {
            _logger?.LogDebug(error, "Could not access file size for '{Path}'.", item.FullPath);
            return 0L;
        }
    }

    private void DisposeAllWatches()
    {
        foreach (var (key, _) in _watches.ToArray())
        {
            if (_watches.TryRemove(key, out var removed))
            {
                removed.Watcher.Dispose();
            }
        }
    }

    private static string WatchKey(string brainId, string appKey, string slot) => $"{brainId}\0{appKey}\0{slot}";

    private sealed record WatchEntry(
        string BrainId,
        string AppKey,
        string Slot,
        string NormalizedRoot,
        FileSystemWatcher Watcher);

    private sealed record PendingChange(
        string BrainId,
        string Slot,
        string FullPath,
        string RelativePath,
        string Kind,
        DateTimeOffset LastSeenUtc);
}
