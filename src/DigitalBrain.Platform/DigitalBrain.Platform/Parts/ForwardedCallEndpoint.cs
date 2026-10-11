using System.Diagnostics;
using System.Runtime.CompilerServices;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// The holder's side of a forwarded call (T20-12). Before anything executes: the link's machine must serve the
// brain's owner or a member account, and the forwarded caller's account (R55/R95/R98); this machine must hold the
// part's live lease on the head (R87), opening the part when the lease is ours; with the sequencer unreachable an
// Own machine trusts its local lease (it journals, Q26) and a Cloud machine answers 503 (R89). Otherwise NotHolder.
// The call is re-invoked on the local grain reference (R83) under the forwarded RequestContext, the caller
// re-stamped as forwarded by the link's machine, inside a server activity parented on the forwarded traceparent.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class ForwardedCallEndpoint(PartRoutes routes, PartLogs logs, ISequencerClient sequencer, INodeDirectoryClient directory,
    IKeyringClient keyring, IIdentityClient identity, MachineIdentity machine, IGrainFactory grains, IGrainReferenceRuntime runtime,
    Serializer serializer, IOptions<PartsOptions> options, TimeProvider time)
{
    private readonly ConditionalWeakTable<QuicMachineLink, Task<string[]>> _accounts = [];

    public async Task<LinkReply> Answer(QuicMachineLink link, LinkRequest request, CancellationToken _)
    {
        var call = serializer.Deserialize<ForwardedCall>(request.Payload) ?? throw new InvalidDataException("An empty forwarded call.");
        ForwardedOutcome outcome;
        try { outcome = new(await Run(link, request, call), null); }
        catch (Exception error) { outcome = new(null, error); }
        return new LinkReply(serializer.SerializeToArray(outcome), null, null);
    }

    private async Task<object?> Run(QuicMachineLink link, LinkRequest request, ForwardedCall call)
    {
        var part = routes.PartOf(call.Target) ?? throw new ArgumentException($"{call.Target} belongs to no part; it is never forwarded.");
        var accounts = await _accounts.GetValue(link, peer => Accounts(peer.RemoteMachineId));
        await Admit(link.RemoteMachineId, accounts, part, call);
        await RequireHolder(part);

        var parent = ActivityContext.TryParse(request.TraceParent, request.TraceState, out var forwarded) ? forwarded : default;
        using var activity = PartCallFilter.Source.StartActivity($"ForwardedCall {call.Request.GetInterfaceName()}.{call.Request.GetMethodName()}", ActivityKind.Server, parent);
        activity?.SetTag(PartCallFilter.MachineTag, machine.MachineId);
        activity?.SetTag(PartCallFilter.GrainTag, call.Target.ToString());
        activity?.SetTag(PartCallFilter.PartTag, part.Value);
        activity?.SetTag("digitalbrain.forwarded-by", link.RemoteMachineId);

        RequestContext.Clear();
        foreach (var (key, value) in call.Context) { RequestContext.Set(key, value); }
        if (call.Context.TryGetValue(CallerContextStamper.RequestContextKey, out var stamped) && stamped is CallerContext caller)
        { RequestContext.Set(CallerContextStamper.RequestContextKey, caller with { ForwardedBy = link.RemoteMachineId }); }
        try
        {
            var reference = (GrainReference)grains.GetGrain(call.Target);
            return await runtime.InvokeMethodAsync<object>(reference, call.Request, InvokeMethodOptions.None);
        }
        finally { RequestContext.Clear(); }
    }

    private async Task<string[]> Accounts(string machineId)
        => (await directory.Machine(machineId))?.Accounts
            ?? throw new UnauthorizedAccessException($"Machine {machineId} is not registered; its forwarded calls are refused.");

    private async Task Admit(string machineId, string[] accounts, PartId part, ForwardedCall call)
    {
        if (call.Context.TryGetValue(CallerContextStamper.RequestContextKey, out var stamped) && stamped is CallerContext caller
            && !accounts.Contains(caller.AccountId, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException($"A call forwarded by machine {machineId} for account {caller.AccountId} is refused: the machine does not serve that account."); }
        var owner = await keyring.OwnerOf(part.BrainId);
        if (owner is not null && accounts.Contains(owner, StringComparer.Ordinal)) { return; }
        foreach (var account in accounts)
        {
            if (await identity.Membership(part.BrainId, account) is not null) { return; }
        }
        throw new UnauthorizedAccessException($"A call forwarded by machine {machineId} into brain {part.BrainId} is refused: the machine serves neither its owner nor a member.");
    }

    private async Task RequireHolder(PartId part)
    {
        HeadRecord? head;
        try { head = await sequencer.Get(HeadRecord.HashOf(part.Value)); }
        catch (Exception unreachable) when (unreachable is not UnauthorizedAccessException)
        {
            if (options.Value.Role == MachineRole.Own && logs.HoldsLocally(part)) { return; }
            throw new ServiceUnavailableException($"Part {part}'s head cannot be read and this machine has no journaled lease on it: {unreachable.Message}");
        }
        if (head is null || head.Holder != machine.MachineId || head.LeaseExpiry <= time.GetUtcNow())
        { throw new NotHolderException($"Machine {machine.MachineId} does not hold part {part}."); }
        await logs.For(part);
    }
}
