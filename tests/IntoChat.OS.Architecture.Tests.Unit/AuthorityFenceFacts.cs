namespace IntoChat.OS.Architecture.Tests.Unit;

// D-07: the contracts assembly never sees the authority, which is the identity module (its implementation and its contracts).
public sealed class AuthorityFenceFacts
{
    internal static readonly IReadOnlySet<string> Fenced = new HashSet<string>(StringComparer.Ordinal) { "IntoChat.OS" };

    [Fact]
    public void FencedProjectsDoNotReferenceAuthority()
    {
        var offenders = Fenced.Select(Repository.ProjectNamed)
            .SelectMany(project => Repository.References(project.Path)
                .Where(reference => reference.StartsWith("IntoChat.OS.Modules.Identity", StringComparison.Ordinal))
                .Select(reference => $"{project.Name} -> {reference}"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "The identity module is fenced off the contracts assembly (D-07):\n" + string.Join('\n', offenders));
    }
}
