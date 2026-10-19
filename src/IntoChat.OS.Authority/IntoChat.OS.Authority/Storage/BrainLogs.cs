using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace IntoChat.OS.Authority.Storage;

internal sealed class BrainLogs : IAsyncDisposable
{
    private readonly BrainStorageKeys keys;
    private readonly Serializer serializer;
    private readonly Dictionary<string, Task<BrainLog>> logs = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly BrainLogFormat writeFormat;
    private readonly IBrainLogFiles? files;
    private readonly IBrainLogOwnership ownership;
    private bool disposed;

    public BrainLogs(BrainStorageKeys keys, Serializer serializer, IOptions<BrainStoreOptions> options, IBrainLogFiles? files = null,
        IBrainLogOwnership? ownership = null)
    {
        this.ownership = ownership ?? FileLockBrainLogOwnership.Instance;
        this.files = files;
        this.keys = keys;
        this.serializer = serializer;
        writeFormat = options.Value.WriteFormat;
        Directory.CreateDirectory(options.Value.Directory);
    }

    public async Task<BrainLog> For(string brainId)
    {
        var (log, evicted) = Lookup(brainId);
        // The evicted log's lease release can be a network call; it runs after the gate is free so other brains are not held behind it.
        if (evicted is not null) { await evicted.DisposeAsync(); }
        return await log;
    }

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

    // Claims the brain's ownership (writer lock or lease) before touching its key, so a silo that does not own the brain creates nothing.
    // A failed open is forgotten, so a later call can claim the brain once its owner lets go.
    private async Task<BrainLog> Open(string brainId)
    {
        try
        {
            var directory = keys.DirectoryOf(brainId);
            var lease = await ownership.ClaimAsync(directory, brainId, CancellationToken.None);
            try { return new(directory, brainId, await keys.GetOrCreate(brainId, CancellationToken.None), serializer, writeFormat, lease, files); }
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
