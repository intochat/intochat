using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.ToggleSplitButton;

[GenerateSerializer, Alias("ui.toggle-split-button.option")]
public sealed record ToggleSplitButtonOption(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Icon = null)
{
    public ToggleSplitButtonOption() : this("", "", null) { }
}

[GenerateSerializer, Alias("ui.toggle-split-button.state")]
public sealed record ToggleSplitButtonState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Icon,
    [property: Id(3)] bool IsChecked,
    [property: Id(4)] ToggleSplitButtonOption[] Options,
    [property: Id(5)] string? SelectedOptionId,
    [property: Id(6)] bool IsEnabled)
{
    public ToggleSplitButtonState() : this(0, "Toggle", null, false, [], null, true) { }
}

[NeuronAlias("ui.toggle-split-button")]
[DefaultNeuronType("ui.toggle-split-button")]
public interface IToggleSplitButton
{
    Task<ToggleSplitButtonState> Configure(
        string label,
        IReadOnlyList<ToggleSplitButtonOption> options,
        bool isChecked = false,
        string? icon = null,
        string? selectedOptionId = null,
        bool isEnabled = true);
    Task<ToggleSplitButtonState> Toggle();
    Task<ToggleSplitButtonState> SetChecked(bool isChecked);
    Task<ToggleSplitButtonState> SelectOption(string optionId);
    [ReadOnlyCall] Task<ToggleSplitButtonState> Read();
}
