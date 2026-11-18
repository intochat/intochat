using DigitalBrain.Access;

namespace DigitalBrain.Runtime.Orleans.Access;

// How a cluster client process says who it is. A client may only act as a named app on behalf of a person, the same caller a
// sandboxed app gets at the edge; it can never claim the kernel, a module or the scheduler. The silo does not verify this stamp:
// the gateway's own reach (loopback for a local brain) is what makes the client trusted, so a gateway open to other machines needs
// a kernel-issued token first.
public static class ClientCaller
{
    public static IDisposable ActAs(string appKey, string principal, string account, string brain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        var caller = new Caller(new Actor.App(appKey, new Actor.Person(principal, account ?? string.Empty)), new BrainId(brain), conversation: null);
        return OrleansCallerAmbient.Instance.Enter(caller);
    }
}
