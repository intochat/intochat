using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Trust;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Identity.Authority;

// What a machine may write to identity (R63): each call is signed by the machine and, except a registration
// (whose account does not exist yet), honoured only when the machine is bound to the account it acts for.
internal interface IIdentityWrites : IGrainWithStringKey
{
    const string Key = "identity";

    Task<bool> ProvisionOwner(MachineSigned<Member> owner);
    Task<Member?> EnsureMember(MachineSigned<Member> member);
    Task RemoveMember(MachineSigned<MemberRemoval> removal);
    Task ContributeMachine(MachineSigned<CircleMachine> machine);
    Task WithdrawMachine(MachineSigned<CircleMachine> machine);
    Task EnsureAccount(MachineSigned<Account> account);
    Task<bool> EnsureBrain(MachineSigned<Member> owner);
    Task<BrainProvision> CreateBrain(MachineSigned<BrainRequest> request);
    Task<Grant> GrantAsOwner(MachineSigned<OwnerGrant> grant);
    Task RevokeAsOwner(MachineSigned<OwnerRevoke> revoke);
    Task<BrainProvision> RegisterPrincipal(MachineSigned<PrincipalRegistration> registration);
    Task<BrainProvision?> CompleteRegistration(MachineSigned<PrincipalRegistration> registration);
    Task<CallDecision?> Consume(MachineSigned<CallRequest> request);
}

[GenerateSerializer, Alias("identity.brain-request")]
internal sealed record BrainRequest([property: Id(0)] string AccountId, [property: Id(1)] string PrincipalId, [property: Id(2)] string OperationId,
    [property: Id(3)] string DisplayName, [property: Id(4)] BrainKind Kind);

[GenerateSerializer, Alias("identity.member-removal")]
internal sealed record MemberRemoval([property: Id(0)] string BrainScopeId, [property: Id(1)] string AccountId);

[GenerateSerializer, Alias("identity.circle-machine")]
internal sealed record CircleMachine([property: Id(0)] string BrainScopeId, [property: Id(1)] string MachineId);

[GenerateSerializer, Alias("identity.owner-grant")]
internal sealed record OwnerGrant([property: Id(0)] CallerContext Caller, [property: Id(1)] Grant Grant);

[GenerateSerializer, Alias("identity.owner-revoke")]
internal sealed record OwnerRevoke([property: Id(0)] CallerContext Caller, [property: Id(1)] string AppId, [property: Id(2)] string SemanticTypeId, [property: Id(3)] GrantMode Mode);

[GenerateSerializer, Alias("identity.principal-registration")]
internal sealed record PrincipalRegistration([property: Id(0)] string PrincipalId, [property: Id(1)] string DisplayName);

[StatelessWorker, GrainType("identity.writes")]
internal sealed class IdentityWritesGrain : Grain, IIdentityWrites
{
    public async Task<bool> ProvisionOwner(MachineSigned<Member> owner)
    {
        Mesh.Reserved.Refuse(owner.Payload.PrincipalId);
        Mesh.Reserved.Refuse(owner.Payload.AccountId);
        await For(owner, owner.Payload.AccountId);
        return await Authority(owner.Payload).ProvisionOwner(owner.Payload);
    }

    public async Task<Member?> EnsureMember(MachineSigned<Member> member)
    {
        var authority = Authority(member.Payload);
        // R129: a circle's member is written by a machine bound to that member's account or contributed to the circle.
        if (await authority.Kind() == BrainKind.Circle && await authority.Account() is { } owner && owner != member.Payload.AccountId)
        {
            var signer = await Signer(member);
            if (!signer.Accounts.Contains(member.Payload.AccountId, StringComparer.Ordinal) && !(await authority.Machines()).Contains(signer.MachineId, StringComparer.Ordinal))
            { throw new UnauthorizedAccessException($"Machine {signer.MachineId} serves neither account {member.Payload.AccountId} nor circle {member.Payload.BrainId}."); }
        }
        else { await For(member, member.Payload.AccountId); }
        await authority.EnsureMember(member.Payload);
        return await authority.Membership(member.Payload.PrincipalId);
    }

    // R133: EnsureMember's gate (a machine bound to the member's account, or contributed to the circle), plus the
    // brain's owner's machines.
    public async Task RemoveMember(MachineSigned<MemberRemoval> removal)
    {
        var (brain, account) = (removal.Payload.BrainScopeId, removal.Payload.AccountId);
        var signer = await Signer(removal);
        var authority = GrainFactory.GetGrain<IBrainAuthority>(brain);
        var owner = await authority.Account() ?? throw new InvalidOperationException($"Brain {brain} has no owner on record.");
        if (!signer.Accounts.Contains(account, StringComparer.Ordinal) && !signer.Accounts.Contains(owner, StringComparer.Ordinal)
            && !(await authority.Machines()).Contains(signer.MachineId, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException($"Machine {signer.MachineId} may not remove account {account} from brain {brain}."); }
        await authority.RemoveAccount(account);
    }

    // R128: the machine joins when it is bound to an owner or member account of the circle; the signer is that machine,
    // a contributed machine, or a machine of an owner or member account.
    public async Task ContributeMachine(MachineSigned<CircleMachine> machine)
    {
        var (brain, target) = (machine.Payload.BrainScopeId, machine.Payload.MachineId);
        var signer = await Signer(machine);
        var authority = GrainFactory.GetGrain<IBrainAuthority>(brain);
        var accounts = await authority.MemberAccounts();
        var contributed = await GrainFactory.GetGrain<IMachineRegistry>(target).Read()
            ?? throw new UnauthorizedAccessException($"Machine {target} is not registered.");
        if (!contributed.Accounts.Any(accounts.Contains))
        { throw new UnauthorizedAccessException($"Machine {target} serves no owner or member account of circle {brain}."); }
        if (signer.MachineId != target && !signer.Accounts.Any(accounts.Contains) && !(await authority.Machines()).Contains(signer.MachineId, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException($"Machine {signer.MachineId} may not contribute machine {target} to circle {brain}."); }
        await authority.Contribute(target);
    }

    public async Task WithdrawMachine(MachineSigned<CircleMachine> machine)
    {
        var (brain, target) = (machine.Payload.BrainScopeId, machine.Payload.MachineId);
        var signer = await Signer(machine);
        var authority = GrainFactory.GetGrain<IBrainAuthority>(brain);
        if (signer.MachineId != target && !(await authority.Machines()).Contains(signer.MachineId, StringComparer.Ordinal)
            && !(await authority.Account() is { } owner && signer.Accounts.Contains(owner, StringComparer.Ordinal)))
        { throw new UnauthorizedAccessException($"Machine {signer.MachineId} may not withdraw machine {target} from circle {brain}."); }
        await authority.Withdraw(target);
    }

    public async Task EnsureAccount(MachineSigned<Account> account)
    {
        await For(account, account.Payload.AccountId);
        await GrainFactory.GetGrain<IAccount>(account.Payload.AccountId).EnsureCreated(account.Payload);
    }

    public async Task<bool> EnsureBrain(MachineSigned<Member> owner)
    {
        await For(owner, owner.Payload.AccountId);
        return await GrainFactory.GetGrain<IAccount>(owner.Payload.AccountId).EnsureBrain(owner.Payload);
    }

    public async Task<BrainProvision> CreateBrain(MachineSigned<BrainRequest> request)
    {
        await For(request, request.Payload.AccountId);
        var brain = request.Payload;
        return await GrainFactory.GetGrain<IAccount>(brain.AccountId).CreateBrain(brain.PrincipalId, brain.OperationId, brain.DisplayName, brain.Kind);
    }

    public async Task<Grant> GrantAsOwner(MachineSigned<OwnerGrant> grant)
    {
        var caller = grant.Payload.Caller;
        await For(grant, caller.AccountId);
        return await GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(caller.AccountId, caller.BrainId).Id).GrantAsOwner(caller, grant.Payload.Grant);
    }

    public async Task RevokeAsOwner(MachineSigned<OwnerRevoke> revoke)
    {
        var (caller, app, type, mode) = (revoke.Payload.Caller, revoke.Payload.AppId, revoke.Payload.SemanticTypeId, revoke.Payload.Mode);
        await For(revoke, caller.AccountId);
        await GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(caller.AccountId, caller.BrainId).Id).RevokeAsOwner(caller, app, type, mode);
    }

    public async Task<BrainProvision> RegisterPrincipal(MachineSigned<PrincipalRegistration> registration)
    {
        Mesh.Reserved.Refuse(registration.Payload.PrincipalId);
        await Paired(registration);
        return await GrainFactory.GetGrain<IPrincipal>(registration.Payload.PrincipalId).Register(registration.Payload.DisplayName, registration.MachineId);
    }

    public async Task<BrainProvision?> CompleteRegistration(MachineSigned<PrincipalRegistration> registration)
    {
        await Paired(registration);
        return await GrainFactory.GetGrain<IPrincipal>(registration.Payload.PrincipalId).CompleteRegistration(registration.MachineId);
    }

    public async Task<CallDecision?> Consume(MachineSigned<CallRequest> request)
    {
        var caller = request.Payload.Caller;
        await For(request, caller.AccountId);
        return await GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(caller.AccountId, caller.BrainId).Id).Consume(request.Payload);
    }

    private IBrainAuthority Authority(Member member) => GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(member.AccountId, member.BrainId).Id);

    // The machine signed this call and is bound to the account it acts for.
    private async Task For<T>(MachineSigned<T> signed, string account)
    {
        if (signed.Account != account) { throw new UnauthorizedAccessException($"A call signed for {signed.Account} cannot act for {account}."); }
        await GrainFactory.GetGrain<IMachineRegistry>(signed.MachineId).Accept(signed.Call());
    }

    // The machine signed this call (signature, timestamp, counter); what it may do is the caller's check.
    private async Task<RegisteredMachine> Signer<T>(MachineSigned<T> signed)
    {
        var registry = GrainFactory.GetGrain<IMachineRegistry>(signed.MachineId);
        await registry.AcceptMachine(signed.Call());
        return (await registry.Read())!;
    }

    // A registration creates its account, so the machine's signature and replay window are checked (R67). R72's
    // pairing gate is gone: only a registered machine's link reaches this write (T20-4, T20-8).
    private async Task Paired<T>(MachineSigned<T> signed)
        => await GrainFactory.GetGrain<IMachineRegistry>(signed.MachineId).AcceptMachine(signed.Call());
}
