using DigitalBrain.Framework;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Identity.Authority;

// Identity actors are neurons like everything else; the platform ring keeps them off every untrusted edge.
internal abstract class IdentityStateGrain<T>(IPersistentState<T> storage) : Neuron where T : new()
{
    protected T State => storage.State;
    protected async Task Persist(T next)
    {
        var previous = storage.State;
        storage.State = next;
        try { await storage.WriteStateAsync(); }
        catch { storage.State = previous; DeactivateOnIdle(); throw; }
    }
}
