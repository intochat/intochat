using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Identity;

// Restores the previous capability caller. The runtime stamp already saves and restores its keys.
internal static class CallerContextScope
{
    internal static IDisposable Scoped(CapabilityCaller caller)
    {
        if (!CallerContextStamper.IsTrusted(caller))
        {
            throw new UntrustedCallerException($"Caller kind {caller.Kind} cannot be stamped by {caller.StampedBy}.");
        }
        return RuntimeCallerContext.Stamp(caller);
    }
}
