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

    // R125: a brain is recorded with its kind; a circle brain is created for its creator's account.
    Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind);

    Task<BrainProvision> EnsureBrain(Member owner);

    Task<BrainProvision> ProvisionOwner(Member owner);

    Task<bool> IsRegistered(string principalId);

    // The machine that registered a principal (R67); only it holds the principal's credential.
    Task<string?> RegisteredBy(string principalId);

    Task<BrainProvision> RegisterPrincipal(string principalId, string displayName);

    Task<BrainProvision?> CompleteRegistration(string principalId);

    Task<BrainEntry[]> BrainsOf(string principalId);

    Task<BrainKind> KindOf(string brainScopeId);

    Task<Member?> Membership(string brainScopeId, string principalId);

    Task<Member?> EnsureMember(Member member);

    // R133: the same gate as EnsureMember, plus the brain's owner. Every membership of the account goes; the owner's
    // account is never removed.
    Task RemoveMember(string brainScopeId, string accountId);

    // R128: a circle brain's contributed machines count as its owner machines, for that brain only. A machine is
    // contributed when it is bound to an owner or member account of the circle.
    Task ContributeMachine(string circleBrainId, string machineId);

    Task WithdrawMachine(string circleBrainId, string machineId);

    Task<string[]> MachinesOf(string circleBrainId);

    // The machine this client signs as; null on a host that is not a machine.
    string? MachineId { get; }

    Task<CallDecision?> Authorize(CallRequest request);

    Task<Grant[]> ListGrants(string brainScopeId);

    Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);

    Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
}

public enum BrainKind { Personal, Circle }

[GenerateSerializer, Alias("identity.brain-entry")]
public sealed record BrainEntry([property: Id(0)] string Id, [property: Id(1)] BrainKind Kind);

[GenerateSerializer, Alias("identity.brain-provision")]
public sealed record BrainProvision([property: Id(0)] Member Member, [property: Id(1)] bool NewBrain);
