using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Identity.Authority;

internal interface IPrincipal : IGrainWithStringKey
{
    Task<Member> Register(string password, string displayName);
    Task<Member?> Authenticate(string password);
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
    Task<Member> CreateBrain(string principalId, string operationId, string displayName);
    Task EnsureBrain(Member owner);
}

internal interface IBrainAuthority : IGrainWithStringKey
{
    Task Initialize(string accountId, string brainId);
    Task<Member?> Membership(string principalId);
    Task EnsureMember(Member member);
    Task ProvisionOwner(Member owner);
    Task RemoveMember(string principalId);
    Task<CallDecision?> Authorize(CallRequest request);
    Task<Grant> Grant(Grant grant);
    Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);
    Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
    Task Revoke(string appId, string semanticTypeId, GrantMode mode);
    Task<Grant[]> ListGrants();
}
