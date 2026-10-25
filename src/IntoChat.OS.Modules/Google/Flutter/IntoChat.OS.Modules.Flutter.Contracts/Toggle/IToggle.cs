using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Toggle;

[NeuronAlias("toggle"), DefaultNeuronType(UIVocabulary.ToggleType)]
public interface IToggle
{
    Task Set(string label, bool on);
    Task Flip();
    [ReadOnlyCall] Task<ToggleState> Read();
}

[GenerateSerializer, Alias("ui.toggle-state")]
[ExportableState]
public sealed class ToggleState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Label { get; set; } = "";
    [Id(3)] public bool On { get; set; }
}
