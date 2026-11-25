using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Synapses;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Runtime.Orleans;

// The open brain logs of this silo, one per brain, each claimed through the configured ownership
// (or kept purely in RAM while registered as ephemeral until promoted in-place via IBrainResidencyStorage.PromoteAsync).
internal sealed class BrainLogs : IAsyncDisposable, IBrainResidencyStorage
{
    private readonly BrainStorageKeys keys;
    private readonly Serializer serializer;
    private readonly JournalPayloads payloads;
    private readonly TimeProvider time;
    private readonly IRetentionPolicy retention;
    private readonly Dictionary<string, Task<BrainLog>> logs = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly IBrainLogFiles? files;
    private readonly IBrainLogOwnership ownership;
    private readonly BrainStoreOptions storeOptions;
    private readonly IBrainResidencyRegistry? residencyRegistry;
    private bool disposed;

    internal Action<BrainLog>? OnLogCreated { get; set; }

    public BrainLogs(BrainStorageKeys keys, Serializer serializer, JournalPayloads payloads, TimeProvider time, IRetentionPolicy retention,
        IOptions<BrainStoreOptions> options, IBrainLogOwnership ownership, IBrainLogFiles? files = null, IBrainResidencyRegistry? residencyRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.ownership = ownership;
        this.files = files;
        this.keys = keys;
        this.serializer = serializer;
        this.payloads = payloads;
        this.time = time;
        this.retention = retention;
        this.storeOptions = options.Value;
        this.residencyRegistry = residencyRegistry;
        Directory.CreateDirectory(options.Value.Directory);
    }

    public async Task<BrainLog> For(string brainId)
    {
        var (log, evicted) = Lookup(brainId);
        // The evicted log's lease release can be a network call; it runs after the gate is free so other brains are not held behind it.
        if (evicted is not null) { await evicted.DisposeAsync(); }
        return await log;
    }

    public BrainLog? TryOpen(string brainId)
    {
        lock (gate)
        {
            if (disposed) { return null; }
            if (logs.TryGetValue(brainId, out var task) && task.IsCompletedSuccessfully)
            {
                var log = task.Result;
                return log.Usable ? log : null;
            }
            return null;
        }
    }

    public IReadOnlyCollection<BrainLog> Opened
    {
        get
        {
            lock (gate)
            {
                if (disposed) { return []; }
                return [.. logs.Values.Where(t => t.IsCompletedSuccessfully && t.Result.Usable).Select(t => t.Result)];
            }
        }
    }

    public async Task PromoteAsync(string brainId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var log = await For(brainId).WaitAsync(ct).ConfigureAwait(false);
        if (!log.IsEphemeral) { return; }

        var directory = keys.DirectoryOf(brainId);
        var lease = await ownership.ClaimAsync(directory, brainId, ct).ConfigureAwait(false);
        try
        {
            var key = await keys.GetOrCreate(brainId, ct).ConfigureAwait(false);
            await log.PromoteToDurableAsync(directory, key, lease, ct).ConfigureAwait(false);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task EvictAsync(string brainId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        Task<BrainLog>? removed = null;
        lock (gate)
        {
            if (disposed) { return; }
            if (logs.TryGetValue(brainId, out var existing))
            {
                // Never evict a brain that has already been promoted to durable storage.
                if (existing.IsCompletedSuccessfully && !existing.Result.IsEphemeral) { return; }
                logs.Remove(brainId);
                removed = existing;
            }
        }

        if (removed is null) { return; }
        BrainLog log;
        try { log = await removed.WaitAsync(ct).ConfigureAwait(false); }
        catch { return; }
        if (log.IsEphemeral)
        {
            await log.DisposeAsync().ConfigureAwait(false);
        }
    }

    public long GetMemoryUsedBytes(string brainId)
        => TryOpen(brainId)?.MemoryUsedBytes ?? 0;

    private (Task<BrainLog> Log, BrainLog? Evicted) Lookup(string brainId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            BrainLog? evicted = null;
            if (logs.TryGetValue(brainId, out var log))
            {
                // A log that lost its ownership or failed a write is dropped and claimed afresh; its release runs outside the gate.
                if (!(log.IsCompletedSuccessfully && log.Result is { Usable: false } stale)) { return (log, null); }
                logs.Remove(brainId);
                evicted = stale;
            }
            log = Open(brainId);
            if (!log.IsFaulted) { logs[brainId] = log; } // Opens usually complete synchronously; a refused claim is not cached.
            return (log, evicted);
        }
    }

    // Claims the brain's ownership before touching its key, so a silo that does not own the brain creates nothing.
    // Ephemeral brains bypass disk/lease/key creation completely and live strictly in RAM until PromoteAsync.
    private async Task<BrainLog> Open(string brainId)
    {
        try
        {
            if (residencyRegistry is not null && residencyRegistry.TryGetEphemeral(brainId, out var ephemeral))
            {
                var ramLog = BrainLog.CreateEphemeral(brainId, ephemeral.MaxMemoryBytes, serializer, payloads, time, retention, files, storeOptions);
                OnLogCreated?.Invoke(ramLog);
                return ramLog;
            }

            var directory = keys.DirectoryOf(brainId);
            var lease = await ownership.ClaimAsync(directory, brainId, CancellationToken.None);
            try
            {
                var opened = new BrainLog(directory, brainId, await keys.GetOrCreate(brainId, CancellationToken.None), serializer, payloads, time, retention, lease, files, storeOptions);
                OnLogCreated?.Invoke(opened);
                return opened;
            }
            catch { await lease.DisposeAsync(); throw; }
        }
        catch
        {
            lock (gate) { if (!disposed) { logs.Remove(brainId); } }
            throw;
        }
    }

    internal async Task<bool> CheckStorageAsync(string brainId, CancellationToken ct)
    {
        try { return await (await For(brainId).WaitAsync(ct)).CheckStorage(ct); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        Task<BrainLog>[] opening;
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            opening = [.. logs.Values];
        }
        foreach (var task in opening)
        {
            BrainLog log;
            try { log = await task; }
            catch { continue; } // Failed opens already released their ownership.
            await log.DisposeAsync();
        }
    }
}
