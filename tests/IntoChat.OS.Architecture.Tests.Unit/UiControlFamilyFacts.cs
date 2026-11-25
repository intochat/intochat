using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The ui.* control family is gone (UI path step X1): controls are control.* children of a screen, every action is a command on
// POST /brains/{id}/act, and the Flutter client draws the generated node views. None of the old ways may come back.
public sealed partial class UiControlFamilyFacts
{
    // The one ui.* neuron left: a read-only reader of the old ui.chat state, which Engineer imports once (OWED.md names it).
    private static readonly string[] LegacyReaders = ["ui.chat"];

    [GeneratedRegex(@"\[Neuron\(""(?<type>ui\.[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex UiNeuronAttribute();

    [GeneratedRegex(@"\b(?:NeuronRef|UiNeuron)\(\s*""ui\.", RegexOptions.CultureInvariant)]
    private static partial Regex UiNeuronMount();

    [GeneratedRegex(@"\binvoke\('", RegexOptions.CultureInvariant)]
    private static partial Regex DartInvoke();

    [Fact]
    public void NoUiControlTypeExists()
    {
        var types = Repository.SourceFiles(Repository.Projects["IntoChat.OS.Modules.UI"].Directory, "*.cs")
            .SelectMany(path => UiNeuronAttribute().Matches(File.ReadAllText(path)).Select(match => match.Groups["type"].Value))
            .Where(type => !LegacyReaders.Contains(type, StringComparer.Ordinal))
            .ToArray();
        Assert.True(types.Length == 0, $"ui.* neuron types in IntoChat.OS.Modules.UI: {string.Join(", ", types)}. A control is a control.* neuron.");
    }

    [Fact]
    public void NoScreenMountsAUiNeuron()
    {
        var mounting = NoProgramLanesFacts.ProductionSources().Where(path => UiNeuronMount().IsMatch(File.ReadAllText(path))).ToArray();
        Assert.True(mounting.Length == 0, $"NeuronRef/UiNeuron mounts of a ui.* kind: {string.Join(", ", mounting)}. Screens mount control.* children.");
    }

    [Fact]
    public void NoDartWidgetCallsANeuronByMethodName()
    {
        var calling = Repository.SourceFiles(Path.Combine(Repository.Root, "ui", "lib"), "*.dart")
            .Where(path => DartInvoke().IsMatch(File.ReadAllText(path))).ToArray();
        Assert.True(calling.Length == 0, $"invoke('…') calls under ui/lib: {string.Join(", ", calling)}. Widgets send commands through the view.");
    }

    // POST /apps/event is deleted: every action is a command on POST /brains/{id}/act. Nothing may name the old route again.
    [Fact]
    public void NothingNamesTheDeletedEventRoute()
    {
        var files = NoProgramLanesFacts.ProductionSources()
            .Concat(Repository.SourceFiles(Repository.FrameworkRoot, "*.cs"))
            .Concat(Repository.SourceFiles(Path.Combine(Repository.Root, "ui", "lib"), "*.dart"));
        var naming = files.Where(path => File.ReadAllText(path).Contains("apps/event", StringComparison.Ordinal)).ToArray();
        Assert.True(naming.Length == 0, $"Sources still name apps/event: {string.Join(", ", naming)}.");
    }
}
