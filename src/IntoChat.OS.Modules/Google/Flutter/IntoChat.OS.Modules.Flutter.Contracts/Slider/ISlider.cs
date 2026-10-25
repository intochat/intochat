using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Slider;

[NeuronAlias("slider"), DefaultNeuronType(UIVocabulary.SliderType)]
public interface ISlider
{
    Task Configure(double min, double max, double step);
    Task SetValue(double value);
    [ReadOnlyCall] Task<SliderState> Read();
}

[GenerateSerializer, Alias("ui.slider-state")]
[ExportableState]
public sealed class SliderState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public double Min { get; set; }
    [Id(3)] public double Max { get; set; } = 1;
    [Id(4)] public double Step { get; set; } = 1;
    [Id(5)] public double Value { get; set; }
}
