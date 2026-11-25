using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The ui.* control family is being removed in steps (control neurons in IntoChat.OS.Modules.UI, their NeuronRef mounts in
// screens, and the Flutter widgets that call a neuron by method name). Each count below may only fall: a step that removes
// some lowers the number in the same commit, and nothing may raise it.
public sealed partial class UiControlRatchetFacts
{
    private const int UiNeuronTypes = 19;
    private const int UiNeuronMounts = 29;
    private const int DartInvokeCalls = 39;

    [GeneratedRegex(@"\[Neuron\(""ui\.", RegexOptions.CultureInvariant)]
    private static partial Regex UiNeuronAttribute();

    [GeneratedRegex(@"\b(?:NeuronRef|UiNeuron)\(\s*""ui\.", RegexOptions.CultureInvariant)]
    private static partial Regex UiNeuronMount();

    [GeneratedRegex(@"\binvoke\('", RegexOptions.CultureInvariant)]
    private static partial Regex DartInvoke();

    [Fact]
    public void UiNeuronTypesInTheUiModuleOnlyFall()
    {
        var count = Count(Repository.SourceFiles(Repository.Projects["IntoChat.OS.Modules.UI"].Directory, "*.cs"), UiNeuronAttribute());
        Assert.True(count <= UiNeuronTypes, $"[Neuron(\"ui.…\")] types in IntoChat.OS.Modules.UI: {count}, ratchet {UiNeuronTypes}. The family only shrinks.");
        Assert.True(count == UiNeuronTypes, $"[Neuron(\"ui.…\")] types fell to {count}: lower {nameof(UiNeuronTypes)} from {UiNeuronTypes} in the same commit.");
    }

    [Fact]
    public void ScreenMountsOfUiNeuronsOnlyFall()
    {
        var count = Count(NoProgramLanesFacts.ProductionSources(), UiNeuronMount());
        Assert.True(count <= UiNeuronMounts, $"NeuronRef/UiNeuron mounts of a ui.* kind in production sources: {count}, ratchet {UiNeuronMounts}. New screens mount control.* children.");
        Assert.True(count == UiNeuronMounts, $"ui.* mounts fell to {count}: lower {nameof(UiNeuronMounts)} from {UiNeuronMounts} in the same commit.");
    }

    [Fact]
    public void DartCallsOfANeuronByMethodNameOnlyFall()
    {
        var lib = Path.Combine(Repository.Root, "ui", "lib");
        var count = Count(Repository.SourceFiles(lib, "*.dart"), DartInvoke());
        Assert.True(count <= DartInvokeCalls, $"invoke('…') calls under ui/lib: {count}, ratchet {DartInvokeCalls}. New widgets send commands through the view, not by method name.");
        Assert.True(count == DartInvokeCalls, $"invoke('…') calls fell to {count}: lower {nameof(DartInvokeCalls)} from {DartInvokeCalls} in the same commit.");
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

    private static int Count(IEnumerable<string> files, Regex pattern) => files.Sum(path => pattern.Count(File.ReadAllText(path)));
}
