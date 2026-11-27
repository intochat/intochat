namespace IntoChat.OS.Architecture.Tests.Unit;

// The OS host layer (IntoChat.OS.Host) is what every OS process and module stands on, so it sits below them all: it never
// references a module or the kernel. The identity module is a leaf the kernel composes; everything else uses its contracts.
public sealed class HostLayerFacts
{
    // The one production project that compiles against the identity implementation: the kernel composes it. Everything else
    // reaches identity through IntoChat.OS.Modules.Identity.Contracts (configuration keys through AuthKeys).
    private static readonly string[] IdentityImplementationUsers =
    [
        "IntoChat.OS.Kernel",
    ];

    [Fact]
    public void TheHostLayerReferencesNoModuleAndNotTheKernel()
    {
        var host = Repository.ProjectNamed("IntoChat.OS.Host");
        var references = Repository.References(host.Path).Concat(Repository.PackageReferences(host.Path));
        var offenders = ForbiddenHostReferences(references, Repository.ComponentNames());
        Assert.True(offenders.Length == 0, "IntoChat.OS.Host must not reference modules, component implementations, or the kernel:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HostLayerChallengerFlagsModulesKernelAndComponentImplementations()
    {
        string[] references =
        [
            "IntoChat.OS",
            "DigitalBrain",
            "IntoChat.OS.Modules.AI",
            "IntoChat.OS.Kernel",
            "IntoChat.OS.Aspire.Hosting",
            "IntoChat.Aspire",
            "IntoChat.Notes",
            "IntoChat.Notes.Contracts",
            "IntoChat.OS.Components.Notes",
            "IntoChat.OS.Components.Notes.Contracts",
        ];

        var offenders = ForbiddenHostReferences(references, ["Aspire", "Notes"]);

        Assert.Equal(
            [
                "IntoChat.Aspire",
                "IntoChat.Notes",
                "IntoChat.OS.Aspire.Hosting",
                "IntoChat.OS.Components.Notes",
                "IntoChat.OS.Kernel",
                "IntoChat.OS.Modules.AI",
            ],
            offenders);
    }

    internal static string[] ForbiddenHostReferences(IEnumerable<string> references, IEnumerable<string> componentNames)
    {
        var componentImplementations = componentNames
            .SelectMany(static name => new[] { $"IntoChat.{name}", $"IntoChat.OS.Components.{name}" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return references
            .Where(reference => reference.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                || reference.StartsWith("IntoChat.OS.Kernel", StringComparison.Ordinal)
                || reference.StartsWith("IntoChat.OS.Aspire", StringComparison.Ordinal)
                || componentImplementations.Contains(reference))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void OnlyTheKernelAndTestsReferenceTheIdentityImplementation()
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
