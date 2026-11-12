using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Units all the way: DigitalBrain.UI.View is the one UI tree. The Flutter module keeps the view, one field neuron per view field,
// the hosted surfaces (web browser, viewport) and its non-widget neurons; the per-widget neurons are gone, and a chat transcript
// and its composer are view nodes.
public sealed partial class OneUiPathFacts
{
    private static readonly string[] Kept =
    [
        "view", "ui.field", "webbrowser", "viewport",
        // Non-UI neurons of the module: a chat's stored projection (rendered as view nodes by its app) and the shell's sources.
        "chat",
        "chat-index", "ui.compute", "ui.problem-reports", "ui.space", "ui.workspace",
    ];

    private static readonly string[] Pending = [];

    [Fact]
    public void NoNeuronOutsideTheKeptUiKinds()
    {
        var aliases = ProductionArchitecture.Types
            .Where(type => type.Assembly.GetName().Name == "IntoChat.OS.Modules.Flutter")
            .Select(type => type.GetCustomAttribute<DigitalBrain.Neurons.NeuronAttribute>(inherit: false)?.Type)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ui.field", aliases);
        var outside = aliases.Except(Kept).Except(Pending).Order(StringComparer.Ordinal).ToArray();
        Assert.True(outside.Length == 0, "Neurons outside the kept UI kinds: " + string.Join(", ", outside));
    }

    [Fact]
    public void OnlyViewAndFieldNeuronKindsRemain()
    {
        Assert.Empty(Pending);
        var kinds = typeof(IntoChat.OS.Modules.Flutter.UIVocabulary)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["confirmation", "ui.field", "view", "viewport", "webbrowser"], kinds);
    }

    // The one leftover of the old UI channel that still ships (file: token); the fact fails when it is gone, so the list only shrinks.
    // Fix-wave item n: a Markdraw/Canvas node in DigitalBrain 0.18.1 replaces the scene card's JSON body and its "_ui" hint.
    private static readonly string[] OldChannelPending =
    [
        "MarkdrawSceneEngine.cs: _ui =",
    ];

    // No UiNode, no ButtonAction and no "_ui" channel anywhere: the server speaks DigitalBrain.UI.View and the client renders it.
    [Fact]
    public void NoUiNodeNoUiChannelNoButtonAction()
    {
        var sources = NoProgramLanesFacts.ProductionSources()
            .Concat(Directory.EnumerateFiles(Path.Combine(Repository.Root, "ui"), "*.dart", SearchOption.AllDirectories)
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is ".dart_tool" or "build" or ".symlinks")));
        var found = sources
            .SelectMany(path => OldUiToken().Matches(File.ReadAllText(path)).Select(match => Path.GetFileName(path) + ": " + match.Value).Distinct())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(OldChannelPending.Order(StringComparer.Ordinal), found);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bUiNode\b|\bButtonAction\b|[""']_ui[""']|\b_ui\s*=")]
    private static partial System.Text.RegularExpressions.Regex OldUiToken();
}
