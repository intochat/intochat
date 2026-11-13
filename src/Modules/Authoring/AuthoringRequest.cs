namespace IntoChat.OS.Modules.Authoring;

/// <summary>
/// Domain request to author or modify a unit/application specification.
/// </summary>
public sealed record AuthoringRequest(
    string Goal,
    string? Alias = null,
    IReadOnlyDictionary<string, string>? ExistingFiles = null,
    string? AppKey = null,
    Guid? Attempt = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Goal))
        {
            throw new ArgumentException("Authoring goal must not be empty.", nameof(Goal));
        }

        if (Alias is not null)
        {
            AuthoringSpec.ValidateAlias(Alias);
        }
    }
}
