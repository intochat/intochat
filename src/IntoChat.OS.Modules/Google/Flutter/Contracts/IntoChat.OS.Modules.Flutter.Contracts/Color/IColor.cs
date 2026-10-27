using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Color;

[NeuronAlias("color"), DefaultNeuronType(UIVocabulary.ColorType)]
public interface IColor
{
    Task Set(string hex);
    [DigitalBrain.ReadOnlyCall] Task<ColorState> Read();
}

[GenerateSerializer, Alias("ui.color-state")]
[ExportableState]
public sealed class ColorState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Hex { get; set; } = "#000000";
}
