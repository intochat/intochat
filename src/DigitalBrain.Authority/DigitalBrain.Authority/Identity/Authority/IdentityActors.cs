using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Authority.Identity.Authority;

internal interface IPrincipal : IGrainWithStringKey
{
    Task<BrainProvision> Register(string displayName);
    Task<BrainProvision?> CompleteRegistration();
    Task<bool> IsRegistered();
    Task<Member?> DefaultMembership();
    Task<BrainEntry[]> Brains();
    // One-way: a brain authority announces membership while the principal may be mid-registration.
    [Orleans.Concurrency.OneWay] Task Joined(string brainId, BrainKind kind);
    [Orleans.Concurrency.OneWay] Task Left(string brainId);
}

internal interface IAccount : IGrainWithStringKey
{
    Task EnsureCreated(Account account);
    Task<Account?> Read();
    Task<BrainProvision> CreateBrain(string principalId, string operationId, string displayName, BrainKind kind);
    // True when this call provisioned the brain's owner; the machine then creates the brain neuron (R57).
    Task<bool> EnsureBrain(Member owner);
}

internal interface IBrainAuthority : IGrainWithStringKey
{
    Task Initialize(string accountId, string brainId);
    // The account that owns this brain; null until the brain is provisioned. Part keys are wrapped for it.
    [Orleans.Concurrency.AlwaysInterleave] Task<string?> Account();
    Task<Member?> Membership(string principalId);
    Task EnsureMember(Member member);
    Task<bool> ProvisionOwner(Member owner);
    Task RemoveMember(string principalId);
    // R125: set once, before the owner is provisioned.
    Task Classify(BrainKind kind);
    [Orleans.Concurrency.AlwaysInterleave] Task<BrainKind> Kind();
    // The accounts of this brain's owner and members (R98).
    [Orleans.Concurrency.AlwaysInterleave] Task<string[]> MemberAccounts();
    // R133: every membership of the account; the owner's account is refused.
    Task RemoveAccount(string accountId);
    // A pure decision (R68): it never provisions and never spends a grant.
    Task<CallDecision?> Authorize(CallRequest request);
    // Spends the Once grants an allowed request rests on, deciding again in the same turn.
    Task<CallDecision?> Consume(CallRequest request);
    Task<Grant> Grant(Grant grant);
    Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);
    Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
    Task Revoke(string appId, string semanticTypeId, GrantMode mode);
    Task<Grant[]> ListGrants();
}
