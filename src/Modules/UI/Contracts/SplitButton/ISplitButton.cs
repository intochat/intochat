using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.SplitButton;

[GenerateSerializer, Alias("ui.split-button.option")]
public sealed record SplitButtonOption(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Icon = null)
{
    public SplitButtonOption() : this("", "", null) { }
}

[GenerateSerializer, Alias("ui.split-button.state")]
public sealed record SplitButtonState(
    [property: Id(0)] int Version,
    [property: Id(1)] string PrimaryLabel,
    [property: Id(2)] string? PrimaryIcon,
    [property: Id(3)] SplitButtonOption[] Options,
    [property: Id(4)] string? SelectedOptionId,
    [property: Id(5)] int InvokeCount)
{
    public SplitButtonState() : this(0, "Run", null, [], null, 0) { }
}

[NeuronAlias("ui.split-button")]
[DefaultNeuronType("ui.split-button")]
public interface ISplitButton
{
    Task<SplitButtonState> Configure(string primaryLabel, IReadOnlyList<SplitButtonOption> options, string? primaryIcon = null, string? selectedOptionId = null);
    Task<SplitButtonState> InvokePrimary();
    Task<SplitButtonState> SelectOption(string optionId);
    [ReadOnlyCall] Task<SplitButtonState> Read();
}
