using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests.Unit;

// No module is exempt from the grammar: every DigitalBrain-prefixed key a module project
// carries lives under DigitalBrain:Modules. The allowlist is closed — each entry is a
// platform-owned contract a module restates at its boundary, keyed by project.
public sealed partial class ModuleKeyGrammarFacts
{
    [GeneratedRegex("\"\\$?DigitalBrain(:|__)(?!Modules[:_])[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex ForeignRootLiteral();

    private static readonly string[] Allowed =
    [
        // Brain-scoped owner identity, not a module option; platform-owned by design review.
        "DigitalBrain.Modules.Flutter.Aspire.Hosting/ShellNames.cs",
    ];

    [Fact]
    public void ModuleKeysLiveUnderTheModulesSection()
    {
        var offenders = Repository.ProjectFiles("*.cs", path => ForeignRootLiteral().IsMatch(File.ReadAllText(path)), project => project.Name.StartsWith("DigitalBrain.Modules.", StringComparison.Ordinal))
            .Select(Repository.ProjectKey)
            .Except(Allowed, StringComparer.Ordinal)
            .Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Module-owned configuration lives under DigitalBrain:Modules:{id}; derive keys from "
            + "DigitalBrainConfiguration.ModuleSection/ModuleOptionsSection instead of a private root:\n"
            + string.Join('\n', offenders));
    }
}
