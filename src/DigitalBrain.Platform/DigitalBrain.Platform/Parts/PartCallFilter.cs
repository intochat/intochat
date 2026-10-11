using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Platform.Parts;

[GenerateSerializer, Alias("platform.parts.forwarded-call")]
internal sealed record ForwardedCall(
    [property: Id(0)] GrainId Target,
    [property: Id(1)] IInvokable Request,
    [property: Id(2)] Dictionary<string, object> Context);

[GenerateSerializer, Alias("platform.parts.forwarded-outcome")]
internal sealed record ForwardedOutcome([property: Id(0)] object? Result, [property: Id(1)] Exception? Error);

// T20-10/T20-12: a call for a part-logged grain whose part is held by another machine runs at that holder, over
// the machines' link, under the caller's trace and RequestContext. A NotHolder reply (nothing executed) or a lost
// lease re-resolves once; a second one is 503. A link failure after the request was sent is not retried.
internal sealed class PartCallFilter(PartRoutes routes, PartRouter router, IMachineTransport transport, Serializer serializer) : IOutgoingGrainCallFilter
{
    public const string Operation = "grain-call";
    public const string SourceName = "DigitalBrain.Machine.Parts";
    public const string MachineTag = "digitalbrain.machine";
    public const string GrainTag = "digitalbrain.grain";
    public const string PartTag = "digitalbrain.part";

    internal static readonly ActivitySource Source = new(SourceName);

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (routes.PartOf(context.TargetId) is not { } part) { await context.Invoke(); return; }
        if (await router.Resolve(part) is not PartRoute.Remote holder) { await context.Invoke(); return; }
        var outcome = await Forward(holder.MachineId, part, context);
        if (outcome.Error is NotHolderException or LeaseLostException)
        {
            router.Forget(part);
            if (await router.Resolve(part) is not PartRoute.Remote next) { await context.Invoke(); return; }
            outcome = await Forward(next.MachineId, part, context);
            if (outcome.Error is NotHolderException or LeaseLostException)
            {
                router.Forget(part);
                throw new ServiceUnavailableException($"Part {part} moved twice while a call was forwarded to it: {outcome.Error.Message}");
            }
        }
        if (outcome.Error is { } error) { ExceptionDispatchInfo.Throw(error); }
        context.Result = outcome.Result;
    }

    private async Task<ForwardedOutcome> Forward(string holder, PartId part, IOutgoingGrainCallContext context)
    {
        using var activity = Source.StartActivity($"PartCall {context.InterfaceName}.{context.MethodName}", ActivityKind.Client);
        activity?.SetTag(MachineTag, transport.LocalMachineId);
        activity?.SetTag(GrainTag, context.TargetId.ToString());
        activity?.SetTag(PartTag, part.Value);
        activity?.SetTag("digitalbrain.holder", holder);
        var call = new ForwardedCall(context.TargetId, context.Request, Entries());
        var traced = Activity.Current;
        var request = new LinkRequest(Operation, serializer.SerializeToArray(call), traced?.Id, traced?.TraceStateString, Guid.NewGuid());
        LinkReply reply;
        try
        {
            var link = await transport.Connect(holder, CancellationToken.None);
            reply = await link.Send(request, CancellationToken.None);
        }
        catch (LinkFailedException failed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failed.Message);
            throw new ServiceUnavailableException($"Part {part}'s holder {holder} could not be reached: {failed.Message}");
        }
        var outcome = serializer.Deserialize<ForwardedOutcome>(Links.LinkReplies.Payload(reply))!;
        if (outcome.Error is { } error) { activity?.SetStatus(ActivityStatusCode.Error, error.Message); }
        return outcome;
    }

    // The RequestContext as this call carries it, the trace included as TraceOutgoingFilter would add it.
    private static Dictionary<string, object> Entries()
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in RequestContext.Entries) { entries[key] = value; }
        if (!entries.ContainsKey(TraceContext.RequestContextKey) && TraceContext.Current is { } trace) { entries[TraceContext.RequestContextKey] = trace; }
        return entries;
    }
}
