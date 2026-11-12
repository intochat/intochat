// os-only: the os neuron state contract; the framework has its own persistence shape, unified in phase 3.
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.State;

namespace IntoChat.OS.Runtime;

/// <summary>A stored record: codec bytes and the store's concurrency tag.</summary>
public sealed record StoredRecord(ReadOnlyMemory<byte> Record, long ETag);

/// <summary>The outcome of a store write.</summary>
public sealed record WriteResult(long ETag, long? Sequence);

// A neuron's persisted record. Signals ride in the same write; the result is the last journal sequence,
// or null when the store journals nothing.
public interface INeuronState<TState> where TState : class
{
    TState Snapshot { get; }

    bool Exists { get; }

    Task<long?> Write(TState next, IReadOnlyList<Signal> signals);

    Task<long?> Clear(IReadOnlyList<Signal> signals);
}

// Where records live: BrainLog on a machine, a dictionary in memory. Records are codec bytes, opaque here.
public interface INeuronStateStore
{
    Task<StoredRecord?> Read(NeuronAddress address, string stateName);

    Task<WriteResult> Write(NeuronAddress address, string stateName, long? expectedETag, ReadOnlyMemory<byte> record, IReadOnlyList<Signal> signals);

    Task<WriteResult> Clear(NeuronAddress address, string stateName, long? expectedETag, IReadOnlyList<Signal> signals);
}
