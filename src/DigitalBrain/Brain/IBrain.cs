using Orleans.Concurrency;

namespace DigitalBrain;

/// <summary>
/// The entrypoint: the space neurons live in, and itself the first neuron. A person has one or
/// more brains; everything a program does starts from one.
/// </summary>
/// <remarks>
/// A brain owns its address space. Every neuron in it has a <see cref="NeuronId"/>, and the brain
/// is the only thing that turns an id into a reference, so no other code builds or parses grain
/// keys. The brain's own identity is its grain key; it carries no name.
/// </remarks>
[Alias("brain"), Orleans.Metadata.DefaultGrainType("brain")]
public interface IBrain : INeuron
{
    /// <summary>
    /// Brings the brain into existence for <paramref name="owner"/>. Publishes <see cref="Created"/>
    /// once and awaits its handlers, so the caller receives a brain that is already provisioned.
    /// Calling it on a created brain changes nothing.
    /// </summary>
    Task Create(Owner owner);

    /// <summary>The brain's state as persisted.</summary>
    Task<State> Read();

    /// <summary>
    /// Resolves a neuron of this brain by id. The reference is valid whether or not the neuron has
    /// ever been touched: neurons exist on first use.
    /// </summary>
    /// <typeparam name="T">The neuron's contract.</typeparam>
    [AlwaysInterleave] Task<T> Neuron<T>(NeuronId id) where T : INeuron;

    /// <summary>
    /// Streams the signals of kind <typeparamref name="T"/> that <paramref name="neuron"/> publishes,
    /// until <paramref name="cancellationToken"/> ends it. Iterating the stream is the connection.
    /// </summary>
    /// <remarks>Interleaves because a watch is long-lived and must never hold the brain's turn.</remarks>
    [AlwaysInterleave] IAsyncEnumerable<T> On<T>(NeuronId neuron, CancellationToken cancellationToken = default) where T : Signal;
}
