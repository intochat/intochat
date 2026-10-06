using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests;

// Configuration keys have typed owners (an options type's SectionName/Key, IntegrationSchema,
// DigitalBrainConfiguration). A raw "DigitalBrain:…" or "DigitalBrain__…" literal outside an
// owner is how the AssistantTitle and identity drifts happened. The baseline is a ratchet: it
// only shrinks. A new file that needs a key either speaks through an existing vocabulary type
// or declares ownership by joining the baseline deliberately.
public sealed partial class ConfigurationKeyFacts
{
    [GeneratedRegex("\"\\$?DigitalBrain(:|__)[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLiteral();

    [Fact]
    public void ConfigurationKeyLiteralsStayWithinTheOwnerBaseline()
    {
        var root = RepositoryRoot();
        var baselinePath = Path.Combine(root, "src", "Testing", "tests", "DigitalBrain.Architecture.Tests", "ConfigurationKeyOwners.json");
        var baseline = JsonSerializer.Deserialize<string[]>(File.ReadAllText(baselinePath))!.ToHashSet(StringComparer.Ordinal);
        var current = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(path => KeyLiteral().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var intruders = current.Except(baseline).Order().ToArray();
        Assert.True(intruders.Length == 0,
            "New configuration key literals outside the owner baseline. Speak through the typed vocabulary "
            + "(an options type's Key, IntegrationSchema.Key, DigitalBrainConfiguration) or deliberately "
            + "add the file to ConfigurationKeyOwners.json:\n" + string.Join('\n', intruders));

        var cured = baseline.Except(current).Order().ToArray();
        Assert.True(cured.Length == 0,
            "These files no longer carry key literals — shrink ConfigurationKeyOwners.json so the ratchet holds:\n"
            + string.Join('\n', cured));
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
