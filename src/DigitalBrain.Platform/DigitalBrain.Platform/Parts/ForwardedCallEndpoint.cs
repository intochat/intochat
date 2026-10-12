using DigitalBrain.Framework.Platform.Connections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// The holder's side of a forwarded call (T20-12, R100). Before anything executes:
// - a stamped caller is trusted, for this brain, of an account the link's machine serves (R55); an unstamped call
//   (kernel and storage work) is admitted only from the brain owner's machines;
// - the link's machine serves the brain's owner or a member account (R95/R98); a member's machine reaches INeuron
//   contracts only — kernel and other cluster-storage grains have no policy of their own, so only the owner's machines
//   reach them;
// - this machine holds the part's live lease on the head (R87), opening the part when the lease is ours; with the
//   sequencer unreachable an Own machine trusts its local lease (it journals, Q26), a Cloud machine answers 503 (R89).
// The RequestContext is rebuilt from the caller stamp (re-stamped as forwarded by the link's machine) and the Trace,
// nothing else; the call runs inside a server activity parented on the forwarded traceparent, on the local grain
// reference (R83). Cancellation does not cross machines (Stage B): forwarded tokens become CancellationToken.None.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class ForwardedCallEndpoint(PartRoutes routes, PartLogs logs, ISequencerClient sequencer, INodeDirectoryClient directory,
    IKeyringClient keyring, IIdentityClient identity, MachineIdentity machine, IGrainFactory grains, IGrainReferenceRuntime runtime,
    GrainClassMap classes, Serializer serializer, IOptions<PartsOptions> options, TimeProvider time, IConnectionBindings bindings)
{
    private static readonly TimeSpan Remembered = TimeSpan.FromSeconds(30);

    private readonly ConditionalWeakTable<QuicMachineLink, Task<string[]>> _accounts = [];
    private readonly ConditionalWeakTable<QuicMachineLink, ConcurrentDictionary<string, (Standing Standing, DateTimeOffset Until)>> _standing = [];

    private readonly ConditionalWeakTable<QuicMachineLink, ConcurrentDictionary<string, (bool Belongs, DateTimeOffset Until)>> _principals = [];

    private enum Standing { None, Member, Owner }

    public async Task<LinkReply> Answer(QuicMachineLink link, LinkRequest request, CancellationToken _)
    {
        byte[] payload;
        try
        {
            var call = serializer.Deserialize<ForwardedCall>(request.Payload) ?? throw new InvalidDataException("An empty forwarded call.");
            payload = serializer.SerializeToArray(new ForwardedOutcome(await Run(link, request, call), null));
        }
        catch (Exception error)
        {
            // R104: an outcome that cannot be serialized (or a failure to read the call) still answers.
            try { payload = serializer.SerializeToArray(new ForwardedOutcome(null, error)); }
            catch (Exception) { payload = serializer.SerializeToArray(new ForwardedOutcome(null, new InvalidOperationException($"{error.GetType().FullName}: {error.Message}"))); }
        }
        return new LinkReply(payload, null, null);
    }

    private async Task<object?> Run(QuicMachineLink link, LinkRequest request, ForwardedCall call)
    {
        var part = routes.PartOf(call.Target) ?? throw new ArgumentException($"{call.Target} belongs to no part; it is never forwarded.");
        // The machine's own kernel and storage work is unstamped, as on the holder itself; only the owner's machines may
        // forward it (R100). A stamped caller must be trusted, for this brain, and of an account the machine serves.
        var caller = call.Caller;
        // R146: an app of another brain reaches this brain's connection only through the binding recorded at Share; it
        // stands as a member would for that call, and the holder's call filter checks the binding again.
        var accounts = await _accounts.GetValue(link, peer => Accounts(peer.RemoteMachineId));
        // R146: the forwarding machine must serve the caller's account, and the binding requires that account to be a
        // member of the caller's brain (the circle) and the connection's owner to be one too (T26-8, R155): a machine
        // stamping a circle it is not in, or a removed member's machine, is refused.
        var bound = caller is not null && CallerContextStamper.IsTrusted(caller) && caller.BrainId != part.BrainId
            && accounts.Contains(caller.AccountId, StringComparer.Ordinal) && await Bound(caller, call.Target, part);
        // R128/R130: a machine contributed to a circle relays a member's request into that member's own brain, so a
        // chosen connection's credential can be opened and re-sealed under the circle's key. Only a contributed
        // machine, for a member of that same circle, is admitted; the caller still stands as a member.
        var relayed = caller is not null && CallerContextStamper.IsTrusted(caller) && !accounts.Contains(caller.AccountId, StringComparer.Ordinal)
            && await Relayed(link, caller, part);
        if (caller is not null && !(CallerContextStamper.IsTrusted(caller) && (caller.BrainId == part.BrainId || bound || relayed)))
        { throw new UnauthorizedAccessException($"A call forwarded by machine {link.RemoteMachineId} carries no trusted caller stamp for brain {part.BrainId}."); }
        if (caller is not null && !accounts.Contains(caller.AccountId, StringComparer.Ordinal) && !relayed)
        { throw new UnauthorizedAccessException($"A call forwarded by machine {link.RemoteMachineId} for account {caller.AccountId} is refused: the machine does not serve that account."); }
        var standing = bound || relayed ? Standing.Member : await StandingOf(link, accounts, part.BrainId);
        if (standing == Standing.None)
        { throw new UnauthorizedAccessException($"A call forwarded by machine {link.RemoteMachineId} into brain {part.BrainId} is refused: the machine serves neither its owner nor a member."); }
        // R107: a non-owner machine speaks only for its users and apps, and only for a principal that belongs to the
        // stamp's account; Platform and Scheduler stamps, or another account's principal, are forgeries from there.
        if (standing != Standing.Owner && caller is not null)
        {
            if (caller.Kind is not (CallerKind.User or CallerKind.App))
            { throw new UnauthorizedAccessException($"A {caller.Kind} caller forwarded by machine {link.RemoteMachineId} is refused: only the owner's machines forward platform or scheduler calls."); }
            if (!await BelongsTo(link, caller.PrincipalId, caller.AccountId))
            { throw new UnauthorizedAccessException($"A call forwarded by machine {link.RemoteMachineId} is refused: principal {caller.PrincipalId} does not belong to account {caller.AccountId}."); }
        }
        if (standing != Standing.Owner && caller is null)
        { throw new UnauthorizedAccessException($"An unstamped call forwarded by machine {link.RemoteMachineId} is refused: only the owner's machines forward kernel work."); }
        if (standing != Standing.Owner && !IsNeuronContract(call))
        { throw new UnauthorizedAccessException($"A call forwarded by machine {link.RemoteMachineId} to {call.Target.Type} is refused: only the owner's machines reach grains that are not neuron contracts."); }
        await RequireHolder(part);

        for (var index = 0; index < call.Request.GetArgumentCount(); index++)
        {
            if (call.Request.GetArgument(index) is CancellationToken) { call.Request.SetArgument(index, CancellationToken.None); }
        }
        var parent = ActivityContext.TryParse(request.TraceParent, request.TraceState, out var forwarded) ? forwarded : default;
        using var activity = PartCallFilter.Source.StartActivity($"ForwardedCall {call.Request.GetInterfaceName()}.{call.Request.GetMethodName()}", ActivityKind.Server, parent);
        activity?.SetTag(PartCallFilter.MachineTag, machine.MachineId);
        activity?.SetTag(PartCallFilter.GrainTag, call.Target.ToString());
        activity?.SetTag(PartCallFilter.PartTag, part.Value);
        activity?.SetTag("digitalbrain.forwarded-by", link.RemoteMachineId);

        RequestContext.Clear();
        if (caller is not null) { RequestContext.Set(CallerContextStamper.RequestContextKey, caller with { ForwardedBy = link.RemoteMachineId }); }
        // A relayed call is the circle core acting inside the member's own brain: its Internal calls are module plumbing.
        if (relayed) { RequestContext.Set(CallerContextStamper.ModuleCallKey, true); }
        if (call.Trace is { } trace) { RequestContext.Set(TraceContext.RequestContextKey, trace); }
        try
        {
            var reference = (GrainReference)grains.GetGrain(call.Target);
            return await runtime.InvokeMethodAsync<object>(reference, call.Request, InvokeMethodOptions.None);
        }
        finally { RequestContext.Clear(); }
    }

    // An app caller of another brain, a connection key of this part's brain, and the binding its owner recorded.
    private async Task<bool> Bound(CallerContext caller, GrainId target, PartId part)
    {
        var key = target.Key.ToString() ?? "";
        if (caller.Kind != CallerKind.App || caller.AppId is not { Length: > 0 } || !ConnectionKeys.Is(key)) { return false; }
        try { if (ConnectionKeys.Parse(key).BrainId != part.BrainId) { return false; } }
        catch (ArgumentException) { return false; }
        await RequireHolder(part);
        return await bindings.IsBound(caller, key);
    }

    // R128/R130: the relay a circle's holder makes for one of its members: the forwarding machine is contributed to a
    // circle brain the caller is a member of. The holder then serves the member's own brain on the member's behalf.
    private async Task<bool> Relayed(QuicMachineLink link, CallerContext caller, PartId part)
    {
        if (caller.Kind is not (CallerKind.User or CallerKind.App)) { return false; }
        BrainEntry[] brains;
        try { brains = await identity.BrainsOf(caller.PrincipalId); }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
        // The member's own brain is the target: a relay never reaches a brain the caller is not in, so a contributed
        // machine cannot use it to open Internal state of another account's brain.
        if (!brains.Any(entry => entry.Id == part.BrainId)) { return false; }
        foreach (var brain in brains.Where(entry => entry.Kind == BrainKind.Circle))
        {
            if ((await identity.MachinesOf(brain.Id)).Contains(link.RemoteMachineId, StringComparer.Ordinal)
                && await identity.Membership(brain.Id, caller.PrincipalId) is not null)
            { return true; }
        }
        return false;
    }

    private bool IsNeuronContract(ForwardedCall call)
        => call.Request.GetInterfaceType() is { } contract && typeof(INeuron).IsAssignableFrom(contract)
            && classes.TryGetGrainClass(call.Target.Type, out var grainClass) && typeof(INeuron).IsAssignableFrom(grainClass);

    private async Task<string[]> Accounts(string machineId)
        => (await directory.Machine(machineId))?.Accounts
            ?? throw new UnauthorizedAccessException($"Machine {machineId} is not registered; its forwarded calls are refused.");

    private async Task<Standing> StandingOf(QuicMachineLink link, string[] accounts, string brainId)
    {
        var known = _standing.GetValue(link, _ => new(StringComparer.Ordinal));
        var now = time.GetUtcNow();
        if (known.TryGetValue(brainId, out var cached) && cached.Until > now) { return cached.Standing; }
        var standing = Standing.None;
        // R128: a machine contributed to a circle brain is an owner machine of that brain only.
        if (await keyring.OwnerOf(brainId) is { } owner && accounts.Contains(owner, StringComparer.Ordinal)
            || (await identity.MachinesOf(brainId)).Contains(link.RemoteMachineId, StringComparer.Ordinal)) { standing = Standing.Owner; }
        else
        {
            foreach (var account in accounts)
            {
                if (await identity.Membership(brainId, account) is not null) { standing = Standing.Member; break; }
            }
        }
        known[brainId] = (standing, now + Remembered);
        return standing;
    }

    // R107: per the identity directory the principal is the account's owning principal; cached like the standing.
    private async Task<bool> BelongsTo(QuicMachineLink link, string principal, string account)
    {
        var known = _principals.GetValue(link, _ => new(StringComparer.Ordinal));
        var key = account + "\n" + principal;
        var now = time.GetUtcNow();
        if (known.TryGetValue(key, out var cached) && cached.Until > now) { return cached.Belongs; }
        var belongs = await identity.ReadAccount(account) is { } record && record.OwnerPrincipalId == principal;
        known[key] = (belongs, now + Remembered);
        return belongs;
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
