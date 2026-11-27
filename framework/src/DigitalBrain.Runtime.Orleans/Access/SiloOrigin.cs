using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Access;

/// <summary>
/// Tells a call this silo process sent from one that arrived through the gateway. The silo's outgoing filter puts a token into
/// the RequestContext of every call it sends; the token is an object of this process, compared by reference, so a cluster
/// client cannot supply it: whatever a client sends under the same key arrives as a different object. The sender's grain id
/// is no such proof, because the gateway does not check the id a client puts on its message.
/// </summary>
/// <remarks>The token does not survive the wire, so a call another silo of the cluster sends counts as arriving from outside.</remarks>
public static class SiloOrigin
{
    private const string Key = "db.internal.silo_origin";
    private static readonly object Token = new();

    /// <summary>True while handling a call that this silo process sent (a grain, a system target or the host's own code).</summary>
    public static bool IsCurrent => ReferenceEquals(RequestContext.Get(Key), Token);

    internal static void Stamp() => RequestContext.Set(Key, Token);

    /// <summary>
    /// Marks the current async execution flow as originating from this silo process until disposed.
    /// Used by in-process silo call filters when admitting internal Orleans runtime callbacks
    /// (such as <c>IGrainTimerInvoker</c> ticks) that bypass <c>IOutgoingGrainCallFilter</c>.
    /// </summary>
    public static IDisposable Enter()
    {
        var prior = RequestContext.Get(Key);
        Stamp();
        return new Restore(prior);
    }

    private sealed class Restore(object? prior) : IDisposable
    {
        public void Dispose()
        {
            if (prior is null) { RequestContext.Remove(Key); }
            else { RequestContext.Set(Key, prior); }
        }
    }
}
