using DigitalBrain.Framework;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Identity.Authority;

// Identity actors are kernel grains, not neurons: no edge can address them.
internal abstract class IdentityStateGrain<T>(IPersistentState<T> storage) : Grain where T : new()
{
    protected T State => storage.State;
    protected async Task Persist(T next)
    {
        var previous = storage.State;
        storage.State = next;
        try { await storage.WriteStateAsync(); }
        catch { storage.State = previous; DeactivateOnIdle(); throw; }
        OnPersisted();
    }

    // Runs only after the write has succeeded, before Persist returns to its caller.
    protected virtual void OnPersisted() { }
}
