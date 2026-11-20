using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Toggle;

[NeuronAlias("ui.toggle")]
[DefaultNeuronType("ui.toggle")]
public interface IToggle
{
    Task<ToggleState> Configure(string label, bool? isOn = null, bool isEnabled = true, string onLabel = "On", string offLabel = "Off");
    Task<ToggleState> Toggle();
    Task<ToggleState> Set(bool isOn);
    Task<ToggleState> SetEnabled(bool isEnabled);

    [ReadOnlyCall]
    Task<ToggleState> Read();
}
