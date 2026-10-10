namespace DigitalBrain;

// The kernel's read-only facts about the caller's brain: which neurons exist and which are awake.
// The brain is always the stamped caller's; there is no way to name another one. An instance is a
// neuron with persisted state: it is recorded on its first write, kernel-stamped, never by module code.
public interface IKernelView
{
    IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron;

    // Of one grain type, or of every type when type is null.
    IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default);

    // The compiled modules' neurons, signals and connectors, then the brain's own features.
    Task<TypeEntry[]> Types(CancellationToken cancellationToken = default);

    // The brains the stamped caller is a member of, the current one included. Ids only: reaching
    // another brain is a new session, never a key from this one.
    Task<string[]> Brains(CancellationToken cancellationToken = default);

    // The caller's instances active somewhere in the cluster right now. Asked on demand; costly.
    Task<Instance[]> Running(CancellationToken cancellationToken = default);
}
