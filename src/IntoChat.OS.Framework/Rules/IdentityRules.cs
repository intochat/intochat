using DigitalBrain.Rules;

namespace IntoChat.OS.Authority.Identity.Rules;

internal sealed record AccountOwnerMatches(string ExistingOwner, string RequestedOwner) : Rule("identity.account-owner-matches")
{
    public override bool Broken => ExistingOwner != RequestedOwner;
    public override string Message => "Account ownership conflict.";
}

internal sealed record PrincipalIdIsValid(string PrincipalId) : Rule("identity.principal-id-valid")
{
    public override bool Broken => PrincipalId.Length is 0 or > 80
        || !System.Text.RegularExpressions.Regex.IsMatch(PrincipalId, "^[a-z0-9][a-z0-9-]*$");
    public override string Message => "Use a lowercase username containing letters, numbers and hyphens.";
}

internal sealed record PrincipalIdIsAvailable(string PrincipalId, bool AlreadyTaken) : Rule("identity.principal-id-available")
{
    // The try- prefix marks a guest (GuestSurface): no registered principal may carry it.
    public override bool Broken => PrincipalId is "owner" or "integrations"
        || PrincipalId.StartsWith(GuestSurface.BrainPrefix, StringComparison.Ordinal) || AlreadyTaken;
    public override string Message => "That username is unavailable.";
}

internal sealed record BrainIsProvisioned(string BrainId, bool Provisioned) : Rule("identity.brain-provisioned")
{
    public override bool Broken => !Provisioned;
    public override string Message => $"Brain {BrainId} is not provisioned.";
}

internal sealed record MemberIsNotBrainOwner(string BrainId, bool IsOwner) : Rule("identity.member-not-brain-owner")
{
    public override bool Broken => IsOwner;
    public override string Message => $"The owner of brain {BrainId} cannot be removed from it.";
}

internal sealed record BrainAuthorityIsInitialized(bool Initialized) : Rule("identity.brain-authority-initialized")
{
    public override bool Broken => !Initialized;
    public override string Message => "Initialize the brain authority before granting access.";
}

internal sealed record MemberIsOwnerRole(bool IsOwner) : Rule("identity.member-is-owner-role")
{
    public override bool Broken => !IsOwner;
    public override string Message => "An owner is required.";
}

internal sealed record BrainOwnerMatches(string BrainId, string CurrentOwner, string RequestedOwner) : Rule("identity.brain-owner-matches")
{
    public override bool Broken => CurrentOwner != RequestedOwner;
    public override string Message => $"Brain {BrainId} already belongs to {CurrentOwner}; {RequestedOwner} cannot be provisioned as its owner.";
}

internal sealed record BrainHasSingleOwner(bool IsDifferentOwner) : Rule("identity.brain-has-single-owner")
{
    public override bool Broken => IsDifferentOwner;
    public override string Message => "A brain has one owner, its creator.";
}

internal sealed record MemberRoleMatches(bool RoleMatches) : Rule("identity.member-role-matches")
{
    public override bool Broken => !RoleMatches;
    public override string Message => "Membership role conflict.";
}

internal sealed record BrainKindIsMutable(string BrainId, string CurrentKind, bool OwnerProvisioned) : Rule("identity.brain-kind-mutable")
{
    public override bool Broken => OwnerProvisioned;
    public override string Message => $"Brain {BrainId} is a {CurrentKind} brain; its kind is fixed.";
}

internal sealed record ContactListNameIsValid(string List, bool MatchesPattern) : Rule("identity.contact-list-name-valid")
{
    public override bool Broken => !MatchesPattern;
    public override string Message => $"Invalid contact list name '{List}'. Must match ^[a-z0-9][a-z0-9-]{{0,39}}$";
}

internal sealed record ContactPrincipalIsNotEmpty(string? Principal) : Rule("identity.contact-principal-not-empty")
{
    public override bool Broken => string.IsNullOrWhiteSpace(Principal);
    public override string Message => "Contact entry must have a non-empty Principal.";
}

internal sealed record ConnectionBelongsToBrain(string ConnectionBrainId, string ExpectedBrainId) : Rule("identity.connection-belongs-to-brain")
{
    public override bool Broken => ConnectionBrainId != ExpectedBrainId;
    public override string Message => "The connection belongs to another brain.";
}

internal sealed record IntegrationIsAvailable(string Kind, bool Available) : Rule("identity.integration-available")
{
    public override bool Broken => !Available;
    public override string Message => $"Integration '{Kind}' is not available.";
}

internal sealed record AuthMethodIsNotBrowserLogin(string ConnectorId, string AuthMethodId, bool IsBrowserLogin) : Rule("identity.auth-method-not-browser-login")
{
    public override bool Broken => IsBrowserLogin;
    public override string Message => $"Authentication method '{AuthMethodId}' of connector '{ConnectorId}' is a browser login; start it from the login route.";
}

internal sealed record ConnectionIsConfigured(bool Configured) : Rule("identity.connection-configured")
{
    public override bool Broken => !Configured;
    public override string Message => "The connection is not configured.";
}

internal sealed record BrainSealerIsAvailable(bool Available) : Rule("identity.brain-sealer-available")
{
    public override bool Broken => !Available;
    public override string Message => "Re-sealing a shared credential needs this machine's part keys.";
}

internal sealed record AuthMethodIsOffered(string ConnectorId, string AuthMethodId, bool Offered) : Rule("identity.auth-method-offered")
{
    public override bool Broken => !Offered;
    public override string Message => $"Connector '{ConnectorId}' does not offer authentication method '{AuthMethodId}'.";
}

internal sealed record CallerCanStoreSecret(bool CanStore) : Rule("identity.caller-can-store-secret")
{
    public override bool Broken => !CanStore;
    public override string Message => "Only the owner can store a secret.";
}

internal sealed record CallerCanRemoveSecret(bool CanRemove) : Rule("identity.caller-can-remove-secret")
{
    public override bool Broken => !CanRemove;
    public override string Message => "Only the owner or trusted platform code can remove a secret.";
}

internal sealed record IssuerIsKnown(string Issuer, bool Known) : Rule("identity.issuer-known")
{
    public override bool Broken => !Known;
    public override string Message => $"External ticket issuer '{Issuer}' is not trusted.";
}
