using System.Collections.Concurrent;
using System.Globalization;
using global::Orleans.Runtime;
using global::Orleans.Serialization;
using global::Orleans.Storage;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

// Grain state over one MemoryStores store: it outlives a silo restart within one process, so a machine
// restart does not take the sequencer, keyring or brain authorities with it.
public sealed class MemoryStoreGrainStorage(ConcurrentDictionary<string, (byte[] State, long ETag)> records, Serializer serializer) : IGrainStorage
{
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        if (records.TryGetValue(Key(stateName, grainId), out var stored))
        {
            grainState.State = serializer.Deserialize<T>(stored.State)!;
            (grainState.ETag, grainState.RecordExists) = (stored.ETag.ToString(CultureInfo.InvariantCulture), true);
        }
        else { (grainState.ETag, grainState.RecordExists) = (null, false); }
        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var key = Key(stateName, grainId);
        lock (records)
        {
            var current = records.TryGetValue(key, out var stored) ? stored.ETag.ToString(CultureInfo.InvariantCulture) : null;
            if (grainState.ETag != current) { throw new InconsistentStateException($"ETag {grainState.ETag} of {key} is stale.", current, grainState.ETag); }
            var next = stored.ETag + 1;
            records[key] = (serializer.SerializeToArray(grainState.State), next);
            (grainState.ETag, grainState.RecordExists) = (next.ToString(CultureInfo.InvariantCulture), true);
        }
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        records.TryRemove(Key(stateName, grainId), out _);
        // A cleared activation reads as a fresh one, as Orleans' memory storage leaves it.
        grainState.State = Activator.CreateInstance<T>();
        (grainState.ETag, grainState.RecordExists) = (null, false);
        return Task.CompletedTask;
    }

    private static string Key(string stateName, GrainId grainId) => $"{grainId}|{stateName}";
}
