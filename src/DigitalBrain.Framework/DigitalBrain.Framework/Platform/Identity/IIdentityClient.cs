using DigitalBrain.Platform.Identity;

namespace DigitalBrain.Framework.Platform.Identity;

// The accounts, principals and brain authorities the trust cluster holds (R58). Passwords never cross:
// the machine hashes and verifies them, the trust side keeps only the hash. A provisioning call reports
// whether the brain is new; the machine then creates the brain neuron itself (R57).
public interface IIdentityClient
{
    Task<Account?> ReadAccount(string accountId);

    Task EnsureAccount(Account account);

    Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName);

    Task<BrainProvision> EnsureBrain(Member owner);

    Task<BrainProvision> ProvisionOwner(Member owner);

    Task<PrincipalCredential?> Credential(string principalId);

    Task<BrainProvision> RegisterPrincipal(string principalId, string passwordHash, string displayName);

    Task<BrainProvision?> CompleteRegistration(string principalId);

    Task SetPasswordHash(string principalId, string passwordHash);

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

[GenerateSerializer, Alias("identity.principal-credential")]
public sealed record PrincipalCredential([property: Id(0)] string PasswordHash, [property: Id(1)] bool RegistrationComplete);
