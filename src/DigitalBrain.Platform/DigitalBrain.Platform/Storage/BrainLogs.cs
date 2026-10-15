using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Storage;

internal sealed class BrainLogs : IAsyncDisposable
{
    private readonly BrainStorageKeys keys;
    private readonly Serializer serializer;
    private readonly FileStream ownership;
    private readonly Dictionary<string, Task<BrainLog>> logs = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private bool disposed;

    public BrainLogs(BrainStorageKeys keys, Serializer serializer, IOptions<BrainStoreOptions> options)
    {
        this.keys = keys;
        this.serializer = serializer;
        Directory.CreateDirectory(options.Value.Directory);
        ownership = new FileStream(Path.Combine(options.Value.Directory, "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public Task<BrainLog> For(string brainId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!logs.TryGetValue(brainId, out var log)) { logs[brainId] = log = Open(brainId); }
            return log;
        }
    }

    private async Task<BrainLog> Open(string brainId)
        => new(keys.DirectoryOf(brainId), brainId, await keys.GetOrCreate(brainId, CancellationToken.None), serializer);

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
        try
        {
            foreach (var task in opening)
            {
                BrainLog log;
                try { log = await task; }
                catch { continue; } // Failed opens already released their writer lock.
                log.Dispose();
            }
        }
        finally { ownership.Dispose(); }
    }
}
