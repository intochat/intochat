using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Platform.Identity;

namespace IntoChat.OS.Authority.Identity;

// The brain's owner as the directory records it: the owner account, whose owner principal is an Owner member of that brain under
// that account. Null when the brain has no owner or the records disagree. Read from the directory, never from a trace or request.
public static class BrainOwners
{
    public static async Task<Account?> VerifiedOwnerAsync(IIdentityClient identity, string brainId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var owner = await identity.OwnerOfBrain(brainId).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (owner is null) { return null; }
        var member = await identity.Membership(brainId, owner.OwnerPrincipalId).WaitAsync(cancellationToken).ConfigureAwait(false);
        return member is not null && member.AccountId == owner.AccountId && member.PrincipalId == owner.OwnerPrincipalId
            && member.BrainId == brainId && member.Role == MemberRole.Owner
            ? owner
            : null;
    }
}
