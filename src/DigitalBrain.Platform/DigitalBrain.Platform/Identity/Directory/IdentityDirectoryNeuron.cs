using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Identity.Directory;

// The identity front door: every operation goes to the directly keyed actor that owns it.
[GrainType("identity-directory")]
internal sealed class IdentityDirectoryNeuron(IIdentityClient identity, PrincipalPasswords passwords) : Grain, IIdentityDirectory
{
    public async Task<Member> RegisterAsync(string principalId, string password, string displayName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await passwords.Register(principalId, password, displayName);
    }
    public async Task<Member?> AuthenticateAsync(string principalId, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(principalId)) { return null; }
        return await passwords.Authenticate(principalId, password);
    }
    public async Task<Member?> FindMemberAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await identity.Membership(BrainScope.Create(accountId, brainId).Id, principalId) is { } member
            && member.AccountId == accountId ? member : null;
    }
    public async Task<string[]> BrainsOfAsync(string principalId, CancellationToken cancellationToken = default)
        => [.. (await identity.BrainsOf(principalId).WaitAsync(cancellationToken)).Select(brain => brain.Id)];
    public async Task<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
        => await FindMemberAsync(principalId, accountId, brainId, cancellationToken) is not null;
    public async Task<Member> ShareBrainAsync(string accountId, string brainId, string principalId, string displayName, MemberRole role, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var member = new Member { AccountId = accountId, BrainId = brainId, PrincipalId = principalId, DisplayName = displayName, Role = role, JoinedAt = DateTimeOffset.UtcNow };
        return (await identity.EnsureMember(member))!;
    }
}
