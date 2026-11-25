using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// One word per concept on the UI path (paper 40 §3): a screen's mounted control is a "child" (wire refKind "child", read at
// /apps/{name}/children/{path}), a record in the tree with no neuron is a "node", a view's change counter is its "generation",
// and "slot" means only a typed dependency ([Slot]). LegacyViewJson is the one place that reads the retired "slot" ref kind.
public sealed partial class ChildVocabularyFacts
{
    // Reads a view stored before the rename: the one place that names the retired ref kind.
    private static readonly string LegacyReader = Path.Combine(Repository.FrameworkRoot, ModuleFolderFacts.SourceFolder, "DigitalBrain.UI", "LegacyViewJson.cs");

    private static IEnumerable<string> Files()
    {
        var roots = new[]
        {
            Path.Combine(Repository.Root, ModuleFolderFacts.SourceFolder),
            Path.Combine(Repository.FrameworkRoot, ModuleFolderFacts.SourceFolder),
            Path.Combine(Repository.Root, "ui", "lib"),
        };
        return roots
            .SelectMany(root => new[] { "*.cs", "*.dart", "*.json" }.SelectMany(pattern => Repository.SourceFiles(root, pattern)))
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(LegacyReader), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_screen_child_is_named_child_on_the_wire_and_in_code()
    {
        var hits = new List<string>();
        foreach (var file in Files())
        {
            var text = File.ReadAllText(file);
            foreach (var rule in Rules)
            {
                foreach (Match match in rule.Pattern.Matches(text))
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    hits.Add($"{Repository.Relative(file)}:{line}: {match.Value} ({rule.Use})");
                }
            }
        }

        Assert.True(hits.Count == 0, $"Retired UI words found ({hits.Count}):\n" + string.Join('\n', hits));
    }

    private static readonly (Regex Pattern, string Use)[] Rules =
    [
        (SlotRefKindJson(), "refKind \"child\""),
        (SlotRefKindCode(), "NeuronRef.ChildKind / 'child'"),
        (SlotsRoute(), "/children/"),
        (RetiredTypes(), "ChildKeys, ScreenNodeItem, Kit.Embed, SettingsChildren"),
        (RevisionAsGeneration(), "Generation = …Generation"),
    ];

    [GeneratedRegex("\"refKind\"\\s*:\\s*\"slot\"")]
    private static partial Regex SlotRefKindJson();

    // C#: NeuronRef("slot", …) or a refKind compared with "slot"; Dart: a 'slot' kind literal (a map read like map['slot'] is a legacy
    // chat message field, not a ref kind).
    [GeneratedRegex("NeuronRef\\(\"slot\"|[Rr]ef[Kk]ind[^\\n]{0,80}==\\s*\"slot\"|(?<!\\[)'slot'")]
    private static partial Regex SlotRefKindCode();

    [GeneratedRegex("/slots/")]
    private static partial Regex SlotsRoute();

    [GeneratedRegex("\\b(SlotKeys|ScreenControlItem|SettingsSlots|InlineSlotsAsync|NoSlots)\\b|\\bKit\\.Slot\\(|public static NeuronRef Slot\\(")]
    private static partial Regex RetiredTypes();

    [GeneratedRegex("\\bRevision\\s*=\\s*[^;,\\n]*\\.Generation\\b")]
    private static partial Regex RevisionAsGeneration();
}
