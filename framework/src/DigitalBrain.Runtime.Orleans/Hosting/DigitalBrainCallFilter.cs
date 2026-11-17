using System.Diagnostics;
using DigitalBrain.Access;
using DigitalBrain.Identity;
using DigitalBrain.Runtime.Orleans.Access;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>
/// Global bidirectional Orleans grain call filter pipeline.
/// Automatically propagates and extracts distributed tracing activities across grain hops,
/// and enforces cross-brain grain boundary invariants.
/// </summary>
/// <remarks>
/// A grain of another brain is refused here, with one exception: the methods of <see cref="INeuronHost"/> that carry a
/// <c>RuntimeInvocation</c>. Only the host grain knows the access of the operation that call names, so it makes the same
/// refusal there (<c>NeuronHostGrainBase</c>) for every operation that is not Shared or Public on a contract marked
/// <see cref="DigitalBrain.Access.ShareableAttribute"/>.
/// </remarks>
public sealed class DigitalBrainCallFilter : IIncomingGrainCallFilter, IOutgoingGrainCallFilter
{
    private const string ActivityIdKey = "intochatos.activity-id";
    private const string TraceparentKey = "traceparent";

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // 1. Extract distributed activity trace id if present
        var traceId = RequestContext.Get(ActivityIdKey) as string ?? RequestContext.Get(TraceparentKey) as string;
        if (!string.IsNullOrWhiteSpace(traceId) && Activity.Current is null)
        {
            var activity = new Activity("Orleans.GrainCall").SetParentId(traceId);
            activity.Start();
        }

        // 2. Validate stamped caller context
        if (OrleansCallerAmbient.Instance.Current is { } caller)
        {
            var targetType = context.TargetId.Type.ToString();
            if (targetType is not null && targetType.StartsWith("neuron", StringComparison.OrdinalIgnoreCase))
            {
                // Verify brain boundary if targeting a brain-scoped grain
                var targetKey = context.TargetId.Key.ToString();
                if (targetKey is not null && CompoundGrainKey.TryParse(targetKey, out var compound))
                {
                    if (!string.Equals(compound.Brain.Value, caller.Brain.Value, StringComparison.Ordinal)
                        && caller.Actor is not Actor.Kernel
                        && !compound.Brain.IsSystem
                        && !HostJudges(context, targetType))
                    {
                        throw Violation(caller.Brain.Value, compound.Brain.Value);
                    }
                }
            }
        }

        // 3. Invoke grain turn
        await context.Invoke();
    }

    private static readonly string[] CallMethods =
    [
        nameof(INeuronHost.IsInterleaved), nameof(INeuronHost.Invoke), nameof(INeuronHost.InvokeInterleaved),
        nameof(INeuronHost.Post), nameof(INeuronHost.PostInterleaved),
    ];

    // Streams stay refused between brains: Orleans enumerates them outside the host's admission.
    private static bool HostJudges(IIncomingGrainCallContext context, string targetType)
        => targetType.StartsWith("neuron.host", StringComparison.Ordinal)
            && context.InterfaceMethod is { } method
            && method.DeclaringType == typeof(INeuronHost)
            && Array.IndexOf(CallMethods, method.Name) >= 0;

    /// <summary>The refusal of a caller stamped in one brain reaching a grain of another.</summary>
    internal static UnauthorizedAccessException Violation(string callerBrain, string targetBrain)
        => new($"Cross-brain security violation: Caller in brain '{callerBrain}' cannot invoke grain in brain '{targetBrain}'.");

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        // 1. Propagate db.caller if present or set from source grain
        if (RequestContext.Get(HostNeuronContext.CallerKey) is null && context.SourceId is { } sourceGrain)
        {
            RequestContext.Set(HostNeuronContext.CallerKey, sourceGrain.Key.ToString() ?? string.Empty);
        }

        // 2. Automatically propagate Activity ID / traceparent
        if (Activity.Current?.Id is { } id)
        {
            RequestContext.Set(ActivityIdKey, id);
            RequestContext.Set(TraceparentKey, id);
        }

        await context.Invoke();
    }
}
