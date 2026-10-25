using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Expander;

[NeuronAlias("expander"), DefaultNeuronType(UIVocabulary.ExpanderType)]
public interface IExpander
{
    Task Set(string header, bool expanded, IReadOnlyList<UiChildRef>? children = null);
    Task Toggle();
    [DigitalBrain.ReadOnlyCall] Task<ExpanderState> Read();
}

[GenerateSerializer, Alias("ui.expander-state")]
[ExportableState]
public sealed class ExpanderState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Header { get; set; } = "";
    [Id(3)] public bool Expanded { get; set; }
    [Id(4)] public List<UiChildRef> Children { get; set; } = [];
}
