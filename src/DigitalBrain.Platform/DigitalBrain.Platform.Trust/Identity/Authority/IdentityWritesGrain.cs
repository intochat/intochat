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
internal sealed record BrainRequest([property: Id(0)] string AccountId, [property: Id(1)] string PrincipalId, [property: Id(2)] string OperationId, [property: Id(3)] string DisplayName);

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
        await For(owner, owner.Payload.AccountId);
        return await Authority(owner.Payload).ProvisionOwner(owner.Payload);
    }

    public async Task<Member?> EnsureMember(MachineSigned<Member> member)
    {
        await For(member, member.Payload.AccountId);
        var authority = Authority(member.Payload);
        await authority.EnsureMember(member.Payload);
        return await authority.Membership(member.Payload.PrincipalId);
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
        return await GrainFactory.GetGrain<IAccount>(brain.AccountId).CreateBrain(brain.PrincipalId, brain.OperationId, brain.DisplayName);
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

    // A registration creates its account, so the machine's signature and replay window are checked (R67). R72's
    // pairing gate is gone: only a registered machine's link reaches this write (T20-4, T20-8).
    private async Task Paired<T>(MachineSigned<T> signed)
        => await GrainFactory.GetGrain<IMachineRegistry>(signed.MachineId).AcceptMachine(signed.Call());
}
