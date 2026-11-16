using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Circles.Rules;

internal sealed record CircleNameIsValid(string CircleName) : Rule("circles.name-valid")
{
    public override bool Broken => !ICircle.IsValidName(CircleName);
    public override string Message => "A circle name is 1 to 40 of a-z, 0-9 and '-'.";
}

internal sealed record AccountIsNotYetInvitedOrMember(string CircleName, string AccountId, CircleRecord Circle) : Rule("circles.account-not-yet-invited-or-member")
{
    public override bool Broken => Circle.Members.Contains(AccountId) || Circle.Invited.Contains(AccountId);
    public override string Message => $"{AccountId} is already in {CircleName} or invited to it.";
}

internal sealed record AccountIsCircleMember(string CircleName, string AccountId, IReadOnlyList<string> Candidates) : Rule("circles.account-is-member")
{
    public override bool Broken => !Candidates.Contains(AccountId);
    public override string Message => $"{AccountId} is not in {CircleName}.";
}

internal sealed record CallerHasCircleInvitation(string CircleName, string AccountId, CircleRecord Circle, string CallerBrain, string CircleBrain) : Rule("circles.caller-has-invitation")
{
    public override bool Broken => !string.Equals(CallerBrain, CircleBrain, StringComparison.Ordinal) || !Circle.Invited.Contains(AccountId);
    public override string Message => $"{AccountId} has no invitation to {CircleName}.";
}

internal sealed record CircleCreatorCannotBeRemoved(string CircleName, string CallerAccount, string OwnerAccount) : Rule("circles.creator-not-removed")
{
    public override bool Broken => CallerAccount != OwnerAccount;
    public override string Message => $"The creator of {CircleName} cannot be removed.";
}

internal sealed record SharedSlotIsConnectorBound(string SlotName, string? Capability) : Rule("circles.shared-slot-connector-bound")
{
    public override bool Broken => Capability is not null;
    public override string Message => $"Slot '{SlotName}' binds capability '{Capability}'; a circle cannot share a capability-bound connection yet.";
}

internal sealed record SharerHasAppToShare(string AccountId, string AppName, int Count, string Brains) : Rule("circles.sharer-has-single-app")
{
    public override bool Broken => Count != 1;
    public override string Message => Count == 0
        ? $"{AccountId} has no app named {AppName} to share."
        : $"{AccountId} has {AppName} in several brains ({Brains}); share it from one.";
}

internal sealed record InviteRoleIsValid(string? Role) : Rule("circles.invite-role-valid")
{
    public override bool Broken => Role is not ("member" or "admin");
    public override string Message => "An invite role is member or admin.";
}

internal sealed record MachineIsUnbound(string? BoundAccountId) : Rule("circles.machine-unbound")
{
    public override bool Broken => BoundAccountId is not null;
    public override string Message => "This machine is already bound to an account.";
}

internal sealed record InviteCodeIsAvailable(InviteCodeRecord? Held) : Rule("circles.invite-code-available")
{
    public override bool Broken => Held is not null;
    public override string Message => "This invite code is already in use.";
}

internal sealed record InviteHmacKeyIsLongEnough(int Length) : Rule("circles.invite-hmac-key-length")
{
    public override bool Broken => Length < 32;
    public override string Message => $"{IInviteKey.Configuration} must be at least 32 bytes.";
}

internal sealed record ComputeSharePercentIsInRange(int Percent) : Rule("circles.compute-share-in-range")
{
    public override bool Broken => Percent is < 0 or > ICircle.MaxComputeSharePercent;
    public override string Message => $"A circle may share 0 to {ICircle.MaxComputeSharePercent}% of its creator's compute.";
}
