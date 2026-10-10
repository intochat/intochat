using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Identity.Authority;

internal interface IPrincipal : IGrainWithStringKey
{
    Task<PrincipalCredential?> Credential();
    Task<BrainProvision> Register(string passwordHash, string displayName);
    Task<BrainProvision?> CompleteRegistration();
    Task SetPasswordHash(string passwordHash);
    Task<Member?> DefaultMembership();
    Task<string[]> Brains();
    // One-way: a brain authority announces membership while the principal may be mid-registration.
    [Orleans.Concurrency.OneWay] Task Joined(string brainId);
    [Orleans.Concurrency.OneWay] Task Left(string brainId);
}

internal interface IAccount : IGrainWithStringKey
{
    Task EnsureCreated(Account account);
    Task<Account?> Read();
    Task<BrainProvision> CreateBrain(string principalId, string operationId, string displayName);
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
    Task<CallDecision?> Authorize(CallRequest request);
    Task<Grant> Grant(Grant grant);
    Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);
    Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
    Task Revoke(string appId, string semanticTypeId, GrantMode mode);
    Task<Grant[]> ListGrants();
}
