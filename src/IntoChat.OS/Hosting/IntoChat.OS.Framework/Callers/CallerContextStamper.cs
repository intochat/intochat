using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Framework;

// A caller is stamped only at a trusted edge: authenticated HTTP, the app proxy or the scheduler. Stamping checks that
// the caller is complete, then writes the runtime's capability-caller keys so they travel with the grain call.
public static class CallerContextStamper
{
    public const string RequestContextKey = "db.cap.principal";

    // A call a module relays inside the cluster (a contributed machine serving a circle member's own brain): the
    // filter admits it as module plumbing, the same a call made through an injected client after leaving a grain.
    public const string ModuleCallKey = "intochatos.trusted-module-call";

    public static CapabilityCaller Stamp(CapabilityCaller context)
    {
        _ = StampScope(context);
        return context;
    }

    public static IDisposable StampScope(CapabilityCaller context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsTrusted(context))
        {
            throw new UntrustedCallerException(
                $"Caller kind {context.Kind} cannot be stamped by {context.StampedBy}.");
        }

        var scope = RuntimeCallerContext.Stamp(context);
        if (TraceContext.Current is null)
        {
            var origin = new DigitalBrain.NeuronId(new DigitalBrain.BrainId(context.BrainId), "edge", "request");
            TraceContext.Enter(TraceContext.Begin(context.AccountId, context.BrainId, origin));
        }
        return scope;
    }

    public static bool TryGet([NotNullWhen(true)] out CapabilityCaller? context)
    {
        context = RuntimeCallerContext.Current;
        return context is not null;
    }

    public static CapabilityCaller Require()
        => TryGet(out var context)
            ? context
            : throw new UntrustedCallerException("No caller context was stamped at a trusted edge.");

    // A call the agent runtime makes on a model's behalf carries the conversation it serves.
    public static bool IsModelOriginated(CapabilityCaller caller) => caller.ConversationId is not null;

    public static bool IsTrusted(CapabilityCaller context) =>
        !string.IsNullOrWhiteSpace(context.PrincipalId)
        && !string.IsNullOrWhiteSpace(context.AccountId)
        && !string.IsNullOrWhiteSpace(context.BrainId)
        && Enum.IsDefined(context.Kind)
        && Enum.IsDefined(context.StampedBy);
}

[GenerateSerializer, Alias("enforcement.untrusted-caller")]
public sealed class UntrustedCallerException(string message) : Exception(message);
