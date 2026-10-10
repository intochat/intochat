using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Identity.Authority;

internal interface IPrincipal : IGrainWithStringKey
{
    [TrustInternal] Task<BrainProvision> Register(string displayName, string machineId);
    [TrustInternal] Task<BrainProvision?> CompleteRegistration(string machineId);
    Task<bool> IsRegistered();
    Task<string?> RegisteredBy();
    Task<Member?> DefaultMembership();
    Task<string[]> Brains();
    // One-way: a brain authority announces membership while the principal may be mid-registration.
    [Orleans.Concurrency.OneWay, TrustInternal] Task Joined(string brainId);
    [Orleans.Concurrency.OneWay, TrustInternal] Task Left(string brainId);
}

internal interface IAccount : IGrainWithStringKey
{
    [TrustInternal] Task EnsureCreated(Account account);
    Task<Account?> Read();
    [TrustInternal] Task<BrainProvision> CreateBrain(string principalId, string operationId, string displayName);
    // True when this call provisioned the brain's owner; the machine then creates the brain neuron (R57).
    [TrustInternal] Task<bool> EnsureBrain(Member owner);
}

internal interface IBrainAuthority : IGrainWithStringKey
{
    [TrustInternal] Task Initialize(string accountId, string brainId);
    // The account that owns this brain; null until the brain is provisioned. Part keys are wrapped for it.
    [Orleans.Concurrency.AlwaysInterleave] Task<string?> Account();
    Task<Member?> Membership(string principalId);
    [TrustInternal] Task EnsureMember(Member member);
    [TrustInternal] Task<bool> ProvisionOwner(Member owner);
    [TrustInternal] Task RemoveMember(string principalId);
    // A pure decision (R68): it never provisions and never spends a grant.
    Task<CallDecision?> Authorize(CallRequest request);
    // Spends the Once grants an allowed request rests on, deciding again in the same turn.
    [TrustInternal] Task<CallDecision?> Consume(CallRequest request);
    [TrustInternal] Task<Grant> Grant(Grant grant);
    [TrustInternal] Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);
    [TrustInternal] Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
    [TrustInternal] Task Revoke(string appId, string semanticTypeId, GrantMode mode);
    Task<Grant[]> ListGrants();
}
