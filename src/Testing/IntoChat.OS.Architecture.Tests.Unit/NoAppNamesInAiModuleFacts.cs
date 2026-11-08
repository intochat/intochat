using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The AI module is app-agnostic: no app name and no app-owned context or tool name appears in its sources. An app
// brings its own vocabulary through the contexts and tools it registers.
public sealed partial class NoAppNamesInAiModuleFacts
{
    [GeneratedRegex(@"\bino\b|photocraft|filmcraft|chart\.|database\.query", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    [Fact]
    public void TheAiModuleNamesNoApp()
    {
        var root = Repository.ProjectNamed("IntoChat.OS.Modules.AI").Directory;
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Repository.Relative(path).Split('/').Any(part => part is "obj" or "bin"))
            .ToList();

        Assert.NotEmpty(sources);

        var offenders = new List<string>();
        foreach (var path in sources)
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var match = Forbidden().Match(lines[index]);
                if (match.Success) { offenders.Add($"{Repository.Relative(path)}:{index + 1}: {match.Value}"); }
            }
        }

        Assert.True(offenders.Count == 0, "The AI module must not name an app; move the name to the app that owns it:\n" + string.Join('\n', offenders));
    }
}
