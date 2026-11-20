namespace IntoChat.OS.Architecture.Tests.Unit;

// D-07: the contracts assembly and the os Framework never see Authority. Framework keeps its name until Phase 6
// retires it (R4-03), so the fence names it directly.
public sealed class AuthorityFenceFacts
{
    internal static readonly IReadOnlySet<string> Fenced = new HashSet<string>(StringComparer.Ordinal) { "IntoChat.OS" };

    [Fact]
    public void FencedProjectsDoNotReferenceAuthority()
    {
        var offenders = Fenced.Select(Repository.ProjectNamed)
            .SelectMany(project => Repository.References(project.Path)
                .Where(reference => reference.StartsWith("IntoChat.OS.Authority", StringComparison.Ordinal))
                .Select(reference => $"{project.Name} -> {reference}"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "Authority is fenced off the contracts and Framework assemblies (D-07):\n" + string.Join('\n', offenders));
    }
}
