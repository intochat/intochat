using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Configuration keys have typed owners (an options type's SectionName/Key, IntegrationSchema,
// IntoChatOSConfiguration). A raw "DigitalBrain:…" or "DigitalBrain__…" literal outside an
// owner is how the AssistantTitle and identity drifts happened. The baseline is a ratchet: it
// only shrinks. A new file that needs a key either speaks through an existing vocabulary type
// or declares ownership by joining the baseline deliberately. Entries are keyed
// "<ProjectName>/<path inside the project>", so a directory move never touches the baseline.
public sealed partial class ConfigurationKeyFacts
{
    [GeneratedRegex("\"\\$?DigitalBrain(:|__)[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLiteral();

    internal static IEnumerable<string> CurrentOwners()
        => Repository.ProjectFiles("*.cs", path => KeyLiteral().IsMatch(File.ReadAllText(path)))
            .Select(Repository.ProjectKey);

    [Fact]
    public void ConfigurationKeyLiteralsStayWithinTheOwnerBaseline()
    {
        var baselinePath = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "ConfigurationKeyOwners.json");
        var baseline = JsonSerializer.Deserialize<string[]>(File.ReadAllText(baselinePath))!.ToHashSet(StringComparer.Ordinal);
        var current = CurrentOwners().ToHashSet(StringComparer.Ordinal);

        var intruders = current.Except(baseline).Order().ToArray();
        Assert.True(intruders.Length == 0,
            "New configuration key literals outside the owner baseline. Speak through the typed vocabulary "
            + "(an options type's Key, IntegrationSchema.Key, IntoChatOSConfiguration) or deliberately "
            + "add the file to ConfigurationKeyOwners.json:\n" + string.Join('\n', intruders));

        var cured = baseline.Except(current).Order().ToArray();
        Assert.True(cured.Length == 0,
            "These files no longer carry key literals — shrink ConfigurationKeyOwners.json so the ratchet holds:\n"
            + string.Join('\n', cured));
    }
}
