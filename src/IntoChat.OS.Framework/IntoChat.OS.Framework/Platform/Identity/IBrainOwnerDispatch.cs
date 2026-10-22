namespace IntoChat.OS.Framework.Platform.Identity;

// Host-owned dispatch for an authenticated external delivery or persisted scheduled work.
// The host resolves identity from its authority; modules supply only the target brain and causal work.
public interface IBrainOwnerDispatch
{
    Task<bool> RunAsync(string brainId, NeuronId origin, Func<Task> work, Trace? trace = null,
        CancellationToken cancellationToken = default);
}
