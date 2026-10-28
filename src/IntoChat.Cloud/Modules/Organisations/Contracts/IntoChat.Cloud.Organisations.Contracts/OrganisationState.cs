using DigitalBrain;

namespace IntoChat.Cloud.Organisations;

public enum OrganisationRole
{
    Admin = 0,
    Member = 1,
}

public enum OrganisationStatus
{
    Active = 0,
    Dissolved = 1,
}

public sealed class OrganisationMember
{
    public string PrincipalId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public OrganisationRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public OrganisationMember Clone() => (OrganisationMember)MemberwiseClone();
}

[DigitalBrain.State.ExportableState]
public sealed class OrganisationState
{
    public string OrgId { get; set; } = "";
    public string OrgBrain { get; set; } = "";
    public string Name { get; set; } = "";
    public OrganisationStatus Status { get; set; }
    public OrganisationMember[] Members { get; set; } = [];
    public string PersonalOwnerPrincipalId { get; set; } = "";
    public long Version { get; set; }

    public OrganisationState Clone()
    {
        var copy = (OrganisationState)MemberwiseClone();
        copy.Members = [.. Members.Select(member => member.Clone())];
        return copy;
    }
}

public sealed class OrgMembershipState
{
    public string OrgId { get; set; } = "";
    public string OrgBrain { get; set; } = "";
    public string EmailHash { get; set; } = "";
    public string PersonalOrgBrain { get; set; } = "";
}

public sealed class OrganisationException(string message) : Exception(message)
{
    public string Code => Message;
}
