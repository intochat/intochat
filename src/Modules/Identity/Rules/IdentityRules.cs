using DigitalBrain.Access;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Rules;

internal sealed record AccountOwnerMatches(string ExistingOwner, string RequestedOwner) : Rule("identity.account-owner-matches")
{
    public override bool Broken => ExistingOwner != RequestedOwner;
    public override string Message => "Account ownership conflict.";
}

internal sealed record PrincipalIdIsValid(string PrincipalId) : Rule("identity.principal-id-valid")
{
    public override bool Broken => PrincipalId.Length is 0 or > 80
        || !Regex.IsMatch(PrincipalId, "^[a-z0-9][a-z0-9-]*$");
    public override string Message => "Use a lowercase username containing letters, numbers and hyphens.";
}

internal sealed record PrincipalIdIsAvailable(string PrincipalId, PrincipalRow? ExistingPrincipal) : Rule("identity.principal-id-available")
{
    // The try- prefix marks a guest (GuestSurface): no registered principal may carry it.
    public override bool Broken => PrincipalId is "owner" or "integrations"
        || PrincipalId.StartsWith(GuestSurface.BrainPrefix, StringComparison.Ordinal)
        || ExistingPrincipal?.DefaultMembership is not null;
    public override string Message => "That username is unavailable.";
}

internal sealed record BrainIsProvisioned(string BrainId, IdentityBrainRow? Brain) : Rule("identity.brain-provisioned")
{
    public override bool Broken => Brain is null;
    public override string Message => $"Brain {BrainId} is not provisioned.";
}

internal sealed record MemberIsNotBrainOwner(string BrainId, string? TargetAccountOrPrincipalId, IdentityBrainRow? Brain, Member? Member) : Rule("identity.member-not-brain-owner")
{
    public override bool Broken => Member?.Role == MemberRole.Owner
        || (Brain is not null && !string.IsNullOrEmpty(TargetAccountOrPrincipalId)
            && (TargetAccountOrPrincipalId == Brain.AccountId
                || string.Equals(TargetAccountOrPrincipalId, Member?.PrincipalId, StringComparison.Ordinal) && Member?.Role == MemberRole.Owner));
    public override string Message => $"The owner of brain {BrainId} cannot be removed from it.";
}

internal sealed record BrainAuthorityIsInitialized(IdentityBrainRow? Brain) : Rule("identity.brain-authority-initialized")
{
    public override bool Broken => Brain is null;
    public override string Message => "Initialize the brain authority before granting access.";
}

internal sealed record MemberIsOwnerRole(Member Member) : Rule("identity.member-is-owner-role")
{
    public override bool Broken => Member.Role != MemberRole.Owner;
    public override string Message => "An owner is required.";
}

internal sealed record BrainOwnerMatches(string BrainId, string CurrentOwner, string RequestedOwner) : Rule("identity.brain-owner-matches")
{
    public override bool Broken => CurrentOwner != RequestedOwner;
    public override string Message => $"Brain {BrainId} already belongs to {CurrentOwner}; {RequestedOwner} cannot be provisioned as its owner.";
}

internal sealed record BrainHasSingleOwner(Member Member) : Rule("identity.brain-has-single-owner")
{
    public override bool Broken => Member.Role == MemberRole.Owner;
    public override string Message => "A brain has one owner, its creator.";
}

internal sealed record MemberRoleMatches(MemberRole ExistingRole, MemberRole RequestedRole) : Rule("identity.member-role-matches")
{
    public override bool Broken => ExistingRole != RequestedRole;
    public override string Message => "Membership role conflict.";
}

internal sealed record BrainKindIsMutable(IdentityBrainRow Brain) : Rule("identity.brain-kind-mutable")
{
    public override bool Broken => Brain.OwnerProvisioned;
    public override string Message => $"Brain {Brain.BrainId} is a {Brain.Kind} brain; its kind is fixed.";
}

internal sealed record ConnectionBelongsToBrain(string ConnectionBrainId, string ExpectedBrainId) : Rule("identity.connection-belongs-to-brain")
{
    public override bool Broken => ConnectionBrainId != ExpectedBrainId;
    public override string Message => "The connection belongs to another brain.";
}

internal sealed record IssuerIsKnown(string Issuer, IReadOnlyList<TrustedIssuer> TrustedIssuers) : Rule("identity.issuer-known")
{
    public override bool Broken => !TrustedIssuers.Any(item => string.Equals(item.Name, Issuer, StringComparison.Ordinal));
    public override string Message => $"External ticket issuer '{Issuer}' is not trusted.";
}
