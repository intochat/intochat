namespace IntoChat.OS.Modules.Authoring;

/// <summary>
/// Domain outcome of an authoring build and deployment pass.
/// </summary>
public sealed record UnitBuildOutcome(
    bool Success,
    string Alias,
    string? Hash,
    IReadOnlyDictionary<string, string> Files,
    string Status,
    string? Detail = null);
