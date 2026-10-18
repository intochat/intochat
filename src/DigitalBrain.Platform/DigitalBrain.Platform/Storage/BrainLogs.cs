using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Storage;

internal sealed class BrainLogs : IAsyncDisposable
{
    private readonly BrainStorageKeys keys;
    private readonly Serializer serializer;
    private readonly Dictionary<string, Task<BrainLog>> logs = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly BrainLogFormat writeFormat;
    private bool disposed;

    public BrainLogs(BrainStorageKeys keys, Serializer serializer, IOptions<BrainStoreOptions> options)
    {
        this.keys = keys;
        this.serializer = serializer;
        writeFormat = options.Value.WriteFormat;
        Directory.CreateDirectory(options.Value.Directory);
    }

    public Task<BrainLog> For(string brainId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (logs.TryGetValue(brainId, out var log)) { return log; }
            log = Open(brainId);
            if (!log.IsFaulted) { logs[brainId] = log; } // Opens usually complete synchronously; a refused claim is not cached.
            return log;
        }
    }

    // Claims the brain's writer lock before touching its key, so a silo that does not own the brain creates nothing.
    // A failed open is forgotten, so a later call can claim the brain once its owner lets go.
    private async Task<BrainLog> Open(string brainId)
    {
        try
        {
            var directory = keys.DirectoryOf(brainId);
            var ownership = BrainLog.Claim(directory, brainId);
            try { return new(directory, brainId, await keys.GetOrCreate(brainId, CancellationToken.None), serializer, writeFormat, ownership); }
            catch { ownership.Dispose(); throw; }
        }
        catch
        {
            lock (gate) { if (!disposed) { logs.Remove(brainId); } }
            throw;
        }
    }

    internal async Task<bool> CheckStorageAsync(string brainId, CancellationToken ct)
    {
        try { return (await For(brainId).WaitAsync(ct)).CheckStorage(ct); }
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
            catch { continue; } // Failed opens already released their writer lock.
            log.Dispose();
        }
    }
}
