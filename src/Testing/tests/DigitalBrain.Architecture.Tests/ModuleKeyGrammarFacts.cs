using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests;

// No module is exempt from the grammar: every DigitalBrain-prefixed key a module project
// carries lives under DigitalBrain:Modules. The allowlist is closed — each entry is either a
// retired-section guard (the literal names what is refused) or a platform-owned contract a
// module restates at its boundary.
public sealed partial class ModuleKeyGrammarFacts
{
    [GeneratedRegex("\"\\$?DigitalBrain(:|__)(?!Modules[:_])[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex ForeignRootLiteral();

    private static readonly string[] Allowed =
    [
        // Retired-section guards and their test fixtures: the legacy root is named to refuse it.
        "src/Modules/AI/DigitalBrain.Modules.AI/AIOptionsRuntime.cs",
        "src/Modules/AI/tests/DigitalBrain.Modules.AI.Tests/AIGrammarFacts.cs",
        "src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Aspire.Hosting/Configuration/FlutterHostOptions.cs",
        "src/Modules/Google/Flutter/tests/DigitalBrain.Modules.Flutter.Tests.Hosting/FlutterHostOptionsGrammarFacts.cs",
        // Brain-scoped owner identity, not a module option; platform-owned by design review.
        "src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Aspire.Hosting/ShellNames.cs",
    ];

    [Fact]
    public void ModuleKeysLiveUnderTheModulesSection()
    {
        var root = RepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Modules"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(path => ForeignRootLiteral().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Except(Allowed, StringComparer.Ordinal)
            .Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Module-owned configuration lives under DigitalBrain:Modules:{id}; derive keys from "
            + "DigitalBrainConfiguration.ModuleSection/ModuleOptionsSection instead of a private root:\n"
            + string.Join('\n', offenders));
    }

    // Dockerfiles and publish profiles project keys as DigitalBrain__ env vars with no compiler
    // behind them; a retired root baked there crash-loops the container against the guard.
    [GeneratedRegex(@"DigitalBrain__(AI|Workspace|Apps|ShippedApps|CSharp|Salesforce|Flutter|Time)(__|=|\b)|DigitalBrain__Microsoft__(GitHub|Aspire)(__|=|\b)", RegexOptions.CultureInvariant)]
    private static partial Regex RetiredEnvRoot();

    [Fact]
    public void NoDeploymentArtifactCarriesARetiredEnvRoot()
    {
        var root = RepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".pubxml", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(path => RetiredEnvRoot().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Retired configuration roots baked into deployment artifacts — migrate to the "
            + "DigitalBrain__Modules__ form:\n" + string.Join('\n', offenders));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("DigitalBrain.slnx was not found.");
    }
}
