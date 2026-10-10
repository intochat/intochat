using DigitalBrain.Platform.Identity;

namespace DigitalBrain.Framework.Platform.Identity;

// The accounts, memberships and brain authorities the trust cluster holds (R58, R62). No credential crosses:
// passwords and their hashes stay on the machine. Every write that acts for an account is signed by the machine
// and honoured only from a machine bound to that account (R63). A provisioning call reports whether the brain is
// new; the machine then creates the brain neuron itself (R57).
public interface IIdentityClient
{
    Task<Account?> ReadAccount(string accountId);

    Task EnsureAccount(Account account);

    Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName);

    Task<BrainProvision> EnsureBrain(Member owner);

    Task<BrainProvision> ProvisionOwner(Member owner);

    Task<bool> IsRegistered(string principalId);

    // The machine that registered a principal (R67); only it holds the principal's credential.
    Task<string?> RegisteredBy(string principalId);

    Task<BrainProvision> RegisterPrincipal(string principalId, string displayName);

    Task<BrainProvision?> CompleteRegistration(string principalId);

    Task<string[]> BrainsOf(string principalId);

    Task<Member?> Membership(string brainScopeId, string principalId);

    Task<Member?> EnsureMember(Member member);

    Task<CallDecision?> Authorize(CallRequest request);

    Task<Grant[]> ListGrants(string brainScopeId);

    Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);

    Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
}

[GenerateSerializer, Alias("identity.brain-provision")]
public sealed record BrainProvision([property: Id(0)] Member Member, [property: Id(1)] bool NewBrain);
