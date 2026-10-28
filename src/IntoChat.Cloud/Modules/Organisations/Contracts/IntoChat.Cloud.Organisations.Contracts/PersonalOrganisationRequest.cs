namespace IntoChat.Cloud.Organisations;

public sealed record PersonalOrganisationRequest(
    string PrincipalId,
    string AccountId,
    string BrainId,
    string DisplayName,
    string Email,
    string Language,
    DateTimeOffset VerifiedAt);

public sealed record PersonalOrganisationResult(
    string OrganisationId,
    OrganisationRole Role,
    bool Created);
