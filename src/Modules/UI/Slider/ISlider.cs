using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Slider;

[NeuronAlias("ui.slider")]
[DefaultNeuronType("ui.slider")]
public interface ISlider : IUiInteractiveControl
{
    Task<SliderState> Configure(string label, double min = 0, double max = 100, double step = 1, double? value = null, bool isEnabled = true, string unit = "");
    Task<SliderState> SetValue(double value);

    [ReadOnlyCall]
    Task<SliderState> Read();
}
