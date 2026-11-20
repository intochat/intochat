using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.RadioButtons;

[NeuronAlias("ui.radio-buttons")]
[DefaultNeuronType("ui.radio-buttons")]
public interface IRadioButtons : IUiInteractiveControl
{
    Task<RadioButtonsState> Configure(string header, IReadOnlyList<RadioOption> options, string? selectedId = null, bool isEnabled = true);
    Task<RadioButtonsState> Select(string optionId);

    [ReadOnlyCall]
    Task<RadioButtonsState> Read();
}
