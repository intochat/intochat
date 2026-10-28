using DigitalBrain;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Connections.Platform.Identity;

// Host-owned dispatch for an authenticated external delivery or persisted scheduled work.
// The host resolves identity from its authority; modules supply only the target brain and causal work.
public interface IBrainOwnerDispatch
{
    Task<bool> RunAsync(string brainId, string originKey, Func<Task> work, Trace? trace = null,
        CancellationToken cancellationToken = default);
}
