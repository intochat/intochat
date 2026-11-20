using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.CheckBox;

[NeuronAlias("ui.checkbox")]
[DefaultNeuronType("ui.checkbox")]
public interface ICheckBox
{
    Task<UiCheckBoxState> Configure(string label, bool? isChecked = null, bool isEnabled = true);
    Task<UiCheckBoxState> Toggle();
    Task<UiCheckBoxState> Set(bool isChecked);

    [ReadOnlyCall]
    Task<UiCheckBoxState> Read();
}
