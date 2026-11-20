using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.ColorPicker;

[NeuronAlias("ui.color-picker")]
[DefaultNeuronType("ui.color-picker")]
public interface IColorPicker : IUiInteractiveControl
{
    Task<ColorPickerState> Configure(string label, string hex = "#1F5EFF", IReadOnlyList<string>? swatches = null, bool isEnabled = true);
    Task<ColorPickerState> SetColor(string hex);

    [ReadOnlyCall]
    Task<ColorPickerState> Read();
}
