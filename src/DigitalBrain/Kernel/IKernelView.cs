namespace DigitalBrain;

/// <summary>
/// The kernel's read-only facts about the caller's brain: which neurons exist and which are awake.
/// </summary>
/// <remarks>
/// The brain is always the stamped caller's; there is no way to name another one. An instance is a
/// neuron with persisted state: it is recorded on its first write, kernel-stamped, never by module code.
/// </remarks>
public interface IKernelView
{
    /// <summary>The instances of <typeparamref name="T"/> in the caller's brain.</summary>
    IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron;

    /// <summary>The instances of one grain type in the caller's brain, or of every type when <paramref name="type"/> is null.</summary>
    IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default);

    /// <summary>Every type the caller's brain knows: the compiled modules' neurons, signals and connectors, then the brain's own features.</summary>
    Task<TypeEntry[]> Types(CancellationToken cancellationToken = default);

    /// <summary>The caller's instances that are active somewhere in the cluster right now. Asked on demand; costly.</summary>
    Task<Instance[]> Running(CancellationToken cancellationToken = default);
}
