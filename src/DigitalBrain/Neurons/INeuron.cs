using Orleans.Concurrency;

namespace DigitalBrain;

/// <summary>
/// A stateful, addressable capability living in a brain: a timer, a button, an inbox, a running
/// program, the brain itself. All durable state lives in neurons, never in a process.
/// </summary>
/// <remarks>
/// The contract is deliberately only "can be watched". What a neuron does is its own contract,
/// derived from this one; how it is reached is the brain's job (<see cref="IDigitalBrain.Get{T}"/>).
/// Every signal a neuron publishes is stamped with its <see cref="NeuronId"/> by the runtime, so
/// provenance is never forged.
/// </remarks>
public interface INeuron : IGrainWithStringKey
{
    /// <summary>
    /// Starts nudging <paramref name="observer"/> whenever this neuron publishes.
    /// </summary>
    /// <returns>
    /// The current activation's id. A watcher compares it on reconnect to tell a resumed activation
    /// from a fresh one.
    /// </returns>
    /// <remarks>
    /// Interleaves because a watcher often starts inside its source's turn (a program deploying its
    /// own connection); registering an observer is synchronous, so interleaving is safe.
    /// </remarks>
    [AlwaysInterleave] Task<Guid> Watch(INeuronObserver observer);

    // Replaces this observer's selection with exact signal type names. An empty selection delivers none.
    [AlwaysInterleave] Task<Guid> Watch(INeuronObserver observer, string[] signals);

    /// <summary>
    /// Stops nudging <paramref name="observer"/>. Safe to call while nudges are being
    /// delivered; interleaves so a turn that tears down a watcher never deadlocks on it.
    /// </summary>
    [AlwaysInterleave] Task Unwatch(INeuronObserver observer);

    /// <summary>
    /// Reads up to <paramref name="limit"/> of this neuron's journalled signals with a sequence
    /// greater than <paramref name="since"/>, oldest first.
    /// </summary>
    /// <remarks>
    /// What a watcher calls after a nudge or on reconnect. Read-only and interleaving: it never
    /// changes state and never waits behind the neuron's own turn.
    /// </remarks>
    [AlwaysInterleave, ReadOnly] Task<SignalPage> Read(long since, int limit);
}
