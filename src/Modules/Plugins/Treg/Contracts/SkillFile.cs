namespace IntoChat.Treg;

/// <summary>
/// A raw <c>SKILL.md</c> document imported from treg (or a local/git skill directory)
/// before projection into a pure-data agent app manifest.
/// </summary>
public sealed record SkillFile(
    string Name,
    string Content,
    string? Source = null);
