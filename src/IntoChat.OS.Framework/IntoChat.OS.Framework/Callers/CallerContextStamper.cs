using System.Diagnostics.CodeAnalysis;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// A caller context is stamped only at a trusted edge: authenticated HTTP, the app proxy or the
// scheduler. Stamping validates that the claimed edge may speak for that caller kind, then places
// the context on the Orleans RequestContext so it travels with the grain call. A principal that
// did not pass through this gate is rejected by the call filter as untrusted.
public static class CallerContextStamper
{
    public const string RequestContextKey = "intochatos.caller-context";

    // A call a module relays inside the cluster (a contributed machine serving a circle member's own brain): the
    // filter admits it as module plumbing, the same a call made through an injected client after leaving a grain.
    public const string ModuleCallKey = "intochatos.trusted-module-call";

    public static CallerContext Stamp(CallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsTrusted(context))
        {
            throw new UntrustedCallerException(
                $"Caller kind {context.Kind} cannot be stamped by {context.StampedBy}.");
        }

        RequestContext.Set(RequestContextKey, context);
        if (TraceContext.Current is null)
        {
            var origin = new NeuronId($"{context.BrainId}/edge");
            TraceContext.Enter(TraceContext.Begin(context.AccountId, context.BrainId, origin));
        }
        return context;
    }

    public static bool TryGet([NotNullWhen(true)] out CallerContext? context)
    {
        context = RequestContext.Get(RequestContextKey) as CallerContext;
        return context is not null;
    }

    public static CallerContext Require()
        => TryGet(out var context)
            ? context
            : throw new UntrustedCallerException("No caller context was stamped at a trusted edge.");

    // A call the agent runtime makes on a model's behalf carries the conversation it serves.
    public static bool IsModelOriginated(CallerContext caller) => caller.ConversationId is not null;

    public static bool IsTrusted(CallerContext context) =>
        !string.IsNullOrWhiteSpace(context.PrincipalId)
        && !string.IsNullOrWhiteSpace(context.AccountId)
        && !string.IsNullOrWhiteSpace(context.BrainId)
        && Enum.IsDefined(context.Kind)
        && Enum.IsDefined(context.StampedBy)
        && IsEdgeAllowed(context.Kind, context.StampedBy);

    private static bool IsEdgeAllowed(CallerKind kind, TrustedEdge edge) => kind switch
    {
        CallerKind.User or CallerKind.Assistant => edge == TrustedEdge.AuthenticatedHttp,
        CallerKind.App => edge == TrustedEdge.AppProxy,
        CallerKind.Scheduler => edge == TrustedEdge.Scheduler,
        CallerKind.Platform => edge == TrustedEdge.Platform,
        _ => false,
    };
}

[GenerateSerializer, Alias("enforcement.untrusted-caller")]
public sealed class UntrustedCallerException(string message) : Exception(message);
