using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Select;

[NeuronAlias("ui.select")]
[DefaultNeuronType("ui.select")]
public interface ISelect
{
    Task<SelectState> Configure(string label, IReadOnlyList<SelectOption> options, string? selectedId = null, bool isEnabled = true);
    Task<SelectState> Select(string optionId);

    [ReadOnlyCall]
    Task<SelectState> Read();
}
