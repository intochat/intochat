using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Storage;

internal sealed class BrainLogs : IDisposable
{
    private readonly BrainStorageKeys keys;
    private readonly Serializer serializer;
    private readonly FileStream ownership;
    private readonly ConcurrentDictionary<string, Lazy<Task<BrainLog>>> logs = new(StringComparer.Ordinal);

    public BrainLogs(BrainStorageKeys keys, Serializer serializer, IOptions<BrainStoreOptions> options)
    {
        this.keys = keys;
        this.serializer = serializer;
        Directory.CreateDirectory(options.Value.Directory);
        ownership = new FileStream(Path.Combine(options.Value.Directory, "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public Task<BrainLog> For(string brainId) => logs.GetOrAdd(brainId, id => new Lazy<Task<BrainLog>>(async () =>
        new BrainLog(keys.DirectoryOf(id), id, await keys.GetOrCreate(id, CancellationToken.None), serializer))).Value;

    public void Dispose()
    {
        foreach (var opening in logs.Values)
        { if (opening.IsValueCreated && opening.Value.IsCompletedSuccessfully) { opening.Value.Result.Dispose(); } }
        ownership.Dispose();
    }
}
