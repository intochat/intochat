namespace IntoChat.OS.Architecture.Tests.Unit;

// The OS host layer (IntoChat.OS.Host) is what every OS process and module stands on, so it sits below them all: it never
// references a module or the kernel. The identity module is a leaf the kernel composes; everything else uses its contracts.
public sealed class HostLayerFacts
{
    // The projects that still compile against the identity implementation. AppHost, Azure and Catalog read AuthOptions,
    // which carries the bootstrap password and so cannot be a contract (OWED.md: a secret-free keys contract retires them).
    private static readonly string[] IdentityImplementationUsers =
    [
        "IntoChat.OS.AppHost",
        "IntoChat.OS.Kernel",
        "IntoChat.OS.Modules.Azure",
        "IntoChat.OS.Modules.Catalog",
    ];

    [Fact]
    public void TheHostLayerReferencesNoModuleAndNotTheKernel()
    {
        var host = Repository.ProjectNamed("IntoChat.OS.Host");
        var offenders = Repository.References(host.Path)
            .Where(reference => reference.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                || reference.StartsWith("IntoChat.OS.Kernel", StringComparison.Ordinal)
                || reference.StartsWith("IntoChat.OS.Aspire", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "IntoChat.OS.Host must not reference modules or the kernel:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void OnlyTheKernelTestsAndTheTrackedHoldoutsReferenceTheIdentityImplementation()
    {
        var tests = Repository.TestProjects().Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var users = Repository.Projects.Values
            .Where(project => Repository.References(project.Path).Contains("IntoChat.OS.Modules.Identity", StringComparer.Ordinal))
            .Select(project => project.Name)
            .Where(name => !tests.Contains(name))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(IdentityImplementationUsers, users);
    }
}
