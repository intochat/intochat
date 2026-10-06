using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests;

// No module is exempt from the grammar: every DigitalBrain-prefixed key a module project
// carries lives under DigitalBrain:Modules. The allowlist is closed — each entry is a
// platform-owned contract a module restates at its boundary.
public sealed partial class ModuleKeyGrammarFacts
{
    [GeneratedRegex("\"\\$?DigitalBrain(:|__)(?!Modules[:_])[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex ForeignRootLiteral();

    private static readonly string[] Allowed =
    [
        // Brain-scoped owner identity, not a module option; platform-owned by design review.
        "src/DigitalBrain.Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Aspire.Hosting/ShellNames.cs",
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
