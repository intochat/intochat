using System.Collections.Concurrent;
using Orleans.Storage;
using DigitalBrain.State;

namespace IntoChat.OS.Runtime.InMemory;

// (address, state name) -> (codec bytes, etag). Writes check the etag, as BrainLog does.
internal sealed class MemoryStateStore : INeuronStateStore
{
    private readonly ConcurrentDictionary<(NeuronAddress, string), StoredRecord> _records = new();
    private long _etags;

    public Task<StoredRecord?> Read(NeuronAddress address, string stateName) =>
        Task.FromResult(_records.TryGetValue((address, stateName), out var record) ? record : null);

    public Task<WriteResult> Write(NeuronAddress address, string stateName, long? expectedETag, ReadOnlyMemory<byte> record, IReadOnlyList<Signal> signals)
    {
        var key = (address, stateName);
        lock (_records)
        {
            Check(key, expectedETag);
            var etag = Interlocked.Increment(ref _etags);
            _records[key] = new StoredRecord(record.ToArray(), etag);
            return Task.FromResult(new WriteResult(etag, null));
        }
    }

    public Task<WriteResult> Clear(NeuronAddress address, string stateName, long? expectedETag, IReadOnlyList<Signal> signals)
    {
        var key = (address, stateName);
        lock (_records)
        {
            Check(key, expectedETag);
            _records.TryRemove(key, out _);
            return Task.FromResult(new WriteResult(Interlocked.Increment(ref _etags), null));
        }
    }

    private void Check((NeuronAddress, string) key, long? expectedETag)
    {
        var current = _records.TryGetValue(key, out var record) ? record.ETag : (long?)null;
        if (current != expectedETag)
        { throw new InconsistentStateException($"State {key.Item2} of {key.Item1} changed underneath this activation.", current?.ToString(System.Globalization.CultureInfo.InvariantCulture), expectedETag?.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    }
}

internal interface IMemoryState
{
    Task Read();
}

// IPersistentState over the memory store and the codec, so a neuron's [PersistentState] constructor works unchanged.
internal sealed class MemoryPersistentState<TState>(MemoryStateStore store, IStateCodec codec, NeuronAddress address, string name)
    : IPersistentState<TState>, IMemoryState
{
    private long? _etag;

    public TState State { get; set; } = Fresh();

    public string? Etag => _etag?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool RecordExists => _etag is not null;

    public async Task Read()
    {
        if (await store.Read(address, name) is { } record)
        {
            State = codec.Deserialize<TState>(record.Record.Span);
            _etag = record.ETag;
        }
        else
        {
            State = Fresh();
            _etag = null;
        }
    }

    public Task ReadStateAsync() => Read();

    public async Task WriteStateAsync() => _etag = (await store.Write(address, name, _etag, codec.Serialize(State), [])).ETag;

    public async Task ClearStateAsync()
    {
        await store.Clear(address, name, _etag, []);
        _etag = null;
        State = Fresh();
    }

    // As the Orleans storage does: the parameterless constructor when there is one, uninitialized otherwise.
    private static TState Fresh() => typeof(TState).GetConstructor(Type.EmptyTypes) is not null
        ? Activator.CreateInstance<TState>()
        : (TState)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TState));
}
