using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.CheckBox;

[NeuronAlias("ui.checkbox")]
[DefaultNeuronType("ui.checkbox")]
public interface ICheckBox
{
    Task<CheckBoxState> Configure(string label, bool? isChecked = null, bool isEnabled = true);
    Task<CheckBoxState> Toggle();
    Task<CheckBoxState> Set(bool isChecked);

    [ReadOnlyCall]
    Task<CheckBoxState> Read();
}
