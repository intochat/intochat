namespace IntoChat.OS;

/// <summary>
/// The entrypoint: a brain as a program sees it. Resolve a neuron, watch a neuron, reach the
/// brain's own neuron. Everything a program does starts here.
/// </summary>
/// <remarks>
/// Resolving is addressing, not a call, so <see cref="Get{T}"/> is synchronous like a grain
/// factory. A brain owns its address space: every neuron in it has a <see cref="NeuronId"/>, and
/// this is the only place an id becomes a reference. Nothing else builds or parses keys.
/// Implemented in-process on the silo and over the edge for sandboxed programs; the same
/// interface either way.
/// </remarks>
public interface IIntoChatOS
{
    /// <summary>
    /// Resolves a neuron of this brain by id. The reference is valid whether or not the neuron has
    /// ever been touched: neurons exist on first use.
    /// </summary>
    /// <typeparam name="T">The neuron's contract (a DigitalBrain neuron contract).</typeparam>
    T Get<T>(NeuronId id) where T : class;

    /// <summary>
    /// Resolves a neuron by an id relative to this brain: the same neuron as
    /// <see cref="Get{T}"/> with <c>"{brain}/{relative}"</c>.
    /// </summary>
    /// <typeparam name="T">The neuron's contract.</typeparam>
    /// <exception cref="InvalidOperationException">This brain is not known, for example no caller is stamped.</exception>
    T Neuron<T>(NeuronId relative) where T : class;

    /// <summary>
    /// Streams the <c>[Signal]</c> records of kind <typeparamref name="T"/> that <paramref name="neuron"/> (a reference from
    /// <see cref="Get{T}"/>) publishes until <paramref name="cancellationToken"/> ends it. Iterating the stream is the connection.
    /// </summary>
    IAsyncEnumerable<T> On<T>(object neuron, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => On<T>(neuron, static () => Task.CompletedTask, cancellationToken);

    // Runs recovery after registration and before yielding queued signals, even if none have arrived.
    IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal;

    // One stream over a set of neurons; which member spoke is the record's publisher. The set follows its registry (see Sources).
    IAsyncEnumerable<T> On<T>(Sources sources, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Sources.Stream<T>(this, sources, cancellationToken);

    // "Any signal means rebuild from state": rebuild runs once after registration, then once per
    // signal, never concurrently; signals during a rebuild coalesce into one following rebuild.
    Task React<T>(object neuron, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => React<T>(Sources.Of(neuron), rebuild, cancellationToken);

    Task React<T>(Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => Sources.React<T>(this, sources, rebuild, cancellationToken);

    /// <summary>
    /// The brain's own neuron: the first one, which publishes <see cref="Created"/> and
    /// <see cref="Activated"/>, so a program can watch its brain like any other neuron.
    /// </summary>
    IBrain Self { get; }

    /// <summary>The kernel's read-only facts about this brain: which neurons exist and which are awake.</summary>
    IKernelView Kernel { get; }
}
