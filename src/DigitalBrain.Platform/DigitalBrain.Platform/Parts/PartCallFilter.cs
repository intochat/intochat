using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Platform.Parts;

[GenerateSerializer, Alias("platform.parts.forwarded-call")]
internal sealed record ForwardedCall(
    [property: Id(0)] GrainId Target,
    [property: Id(1)] IInvokable Request,
    [property: Id(2)] CallerContext? Caller,
    [property: Id(3)] Trace? Trace);

[GenerateSerializer, Alias("platform.parts.forwarded-outcome")]
internal sealed record ForwardedOutcome([property: Id(0)] object? Result, [property: Id(1)] Exception? Error);

// T20-10/T20-12: a call for a part-logged grain whose part is held by another machine runs at that holder, over the
// machines' link, under the caller's trace. Only the caller stamp and the Trace travel (R100); an unstamped call
// (kernel and storage work) travels unstamped, which the holder admits only from the brain owner's machines. NotHolder (raised before anything executed) re-resolves once, a second one is 503; a lost lease is 503
// (R102: it may arise after execution began); a link failure after the request was sent is not retried. One-way
// calls are sent without awaiting their outcome (R103).
internal sealed class PartForwarder(PartRoutes routes, PartRouter router, IMachineTransport transport, Serializer serializer,
    ILogger<PartForwarder> logger)
{
    public PartRoutes Routes => routes;

    public PartRouter Router => router;

    public async Task Route(PartId part, string holder, IGrainCallContext context, Func<Task> local)
    {
        // Kernel and storage work carries no caller; the holder admits that only from the brain owner's machines (R100).
        var call = new ForwardedCall(context.TargetId, context.Request, CallerContextStamper.TryGet(out var caller) ? caller : null, TraceContext.Current);
        if (context.Request.GetMethod()?.GetCustomAttribute<OneWayAttribute>() is not null)
        {
            _ = Task.Run(async () =>
            {
                try { Settle(await Forward(holder, part, context, call), part); }
                catch (Exception error) { logger.LogWarning(error, "A one-way call forwarded to {Holder} for part {Part} failed.", holder, part); }
            });
            context.Result = null;
            return;
        }
        var outcome = await Forward(holder, part, context, call);
        if (outcome.Error is NotHolderException)
        {
            router.Forget(part);
            if (await router.Resolve(part) is not PartRoute.Remote next) { await local(); return; }
            outcome = await Forward(next.MachineId, part, context, call);
            if (outcome.Error is NotHolderException)
            {
                router.Forget(part);
                throw new ServiceUnavailableException($"Part {part} moved twice while a call was forwarded to it: {outcome.Error.Message}");
            }
        }
        context.Result = Settle(outcome, part);
    }

    private object? Settle(ForwardedOutcome outcome, PartId part)
    {
        if (outcome.Error is LeaseLostException lost)
        {
            router.Forget(part);
            throw new ServiceUnavailableException($"Part {part}'s holder lost its lease during a forwarded call: {lost.Message}");
        }
        if (outcome.Error is { } error) { ExceptionDispatchInfo.Throw(error); }
        return outcome.Result;
    }

    private async Task<ForwardedOutcome> Forward(string holder, PartId part, IGrainCallContext context, ForwardedCall call)
    {
        using var activity = PartCallFilter.Source.StartActivity($"PartCall {context.InterfaceName}.{context.MethodName}", ActivityKind.Client);
        activity?.SetTag(PartCallFilter.MachineTag, transport.LocalMachineId);
        activity?.SetTag(PartCallFilter.GrainTag, context.TargetId.ToString());
        activity?.SetTag(PartCallFilter.PartTag, part.Value);
        activity?.SetTag("digitalbrain.holder", holder);
        var traced = Activity.Current;
        var request = new LinkRequest(PartCallFilter.Operation, serializer.SerializeToArray(call), traced?.Id, traced?.TraceStateString, Guid.NewGuid());
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
}

// Calls a grain on this machine makes (and the in-process client's): routed before any activation here.
internal sealed class PartCallFilter(PartForwarder forwarder) : IOutgoingGrainCallFilter
{
    public const string Operation = "grain-call";
    public const string SourceName = "DigitalBrain.Machine.Parts";
    public const string MachineTag = "digitalbrain.machine";
    public const string GrainTag = "digitalbrain.grain";
    public const string PartTag = "digitalbrain.part";

    internal static readonly ActivitySource Source = new(SourceName);

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (forwarder.Routes.PartOf(context.TargetId) is not { } part
            || await forwarder.Router.Resolve(part) is not PartRoute.Remote holder) { await context.Invoke(); return; }
        await forwarder.Route(part, holder.MachineId, context, context.Invoke);
    }
}

// R101: calls arriving through the gateway (the MCPs' Orleans clients) never pass this silo's outgoing filter. Their
// grain is activated here as a stand-in without its part (PartGrainStorage reads nothing for a part held elsewhere),
// the call is forwarded, and the stand-in is dropped: it never serves state, even when the part comes here meanwhile.
internal sealed class PartIncomingCallFilter(PartForwarder forwarder) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // System targets and timer ticks carry no grain id of a part.
        if (context.Grain is not IGrainBase || forwarder.Routes.PartOf(context.TargetId) is not { } part) { await context.Invoke(); return; }
        var standIn = forwarder.Router.IsStandIn(context.TargetId);
        try
        {
            if (await forwarder.Router.Resolve(part) is PartRoute.Remote holder)
            {
                await forwarder.Route(part, holder.MachineId, context, standIn ? () => throw Moved(part) : context.Invoke);
                return;
            }
            if (standIn) { throw Moved(part); }
            await context.Invoke();
        }
        finally
        {
            if (standIn)
            {
                forwarder.Router.ForgetStandIn(context.TargetId);
                context.TargetContext.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, $"A stand-in for part {part}."), CancellationToken.None);
            }
        }
    }

    private static ServiceUnavailableException Moved(PartId part) => new($"Part {part} came to this machine during the call; call again.");
}
