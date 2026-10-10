using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Identity.Directory;

// The directory-backed answer to the brain membership question asked by HTTP route filters.
internal sealed class DirectoryBrainAccess(IIdentityClient identity) : IBrainAccess
{
    public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
        => new(HasMembership(principalId, accountId, brainId, cancellationToken));
    // A brain id is global and belongs to one account; asking under any other account is a miss.
    private async Task<bool> HasMembership(string principalId, string accountId, string brainId, CancellationToken ct)
        => await identity.Membership(BrainScope.Create(accountId, brainId).Id, principalId).WaitAsync(ct) is { } member
            && member.AccountId == accountId;

}

// Composed only in the Open posture: the synthetic owner IS the single tenant, while a real
// principal (a cookie session in development) still answers to the directory.
internal sealed class OpenOwnerBrainAccess(DirectoryBrainAccess directory) : IBrainAccess
{
    public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
        => accountId == AccountSession.DefaultLogin && string.Equals(principalId, AccountSession.DefaultLogin, StringComparison.Ordinal)
            ? new(true)
            : directory.CanAccessAsync(principalId, accountId, brainId, cancellationToken);
}
