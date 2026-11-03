using DigitalBrain.Modules.Configuration;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// No module is exempt from the grammar: every IntoChat.OS-prefixed key a module project
// carries lives under IntoChatOS:Modules. The allowlist is closed — each entry is a
// platform-owned contract a module restates at its boundary, keyed by project.
public sealed partial class ModuleKeyGrammarFacts
{
    [GeneratedRegex("\"\\$?IntoChat.OS(:|__)(?!Modules[:_])[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex ForeignRootLiteral();

    private static readonly string[] Allowed = [];

    [Fact]
    public void ModuleKeysLiveUnderTheModulesSection()
    {
        var offenders = Repository.ProjectFiles("*.cs", path => ForeignRootLiteral().IsMatch(File.ReadAllText(path)), project => project.Name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
            .Select(Repository.ProjectKey)
            .Except(Allowed, StringComparer.Ordinal)
            .Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Module-owned configuration lives under IntoChatOS:Modules:{id}; derive keys from "
            + "ModuleConfiguration.ModuleSection/ModuleOptionsSection instead of a private root:\n"
            + string.Join('\n', offenders));
    }
}
