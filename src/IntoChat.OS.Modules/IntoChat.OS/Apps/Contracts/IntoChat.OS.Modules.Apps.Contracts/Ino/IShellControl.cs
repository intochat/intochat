using System.Text.Json.Serialization;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record ShellStateSnapshot(
    bool WidgetsMenuOpen,
    string[] OpenCardIds,
    string[] PinnedCardIds,
    string? SelectedNeuronId,
    string? ActiveAppId)
{
    public ShellStateSnapshot() : this(false, [], [], null, null) { }
}

[DigitalBrain.Signal("shell.action")]
public sealed record ShellActionSignal(string Action, string? Target = null, string? Payload = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("shell.state.changed")]
public sealed record ShellStateChanged : DigitalBrain.Signal;

/// <summary>
/// Bridge contract between the DigitalBrain runtime (including Ino) and the frontend Flutter desktop shell.
/// </summary>
[NeuronAlias("shell.control"), DefaultNeuronType("shell.control")]
public interface IShellControl
{
    const string DefaultKey = "shell/control";

    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "shell.control", DefaultKey);

    Task<InoToolResult> ToggleWidgetBar(bool? open = null);

    Task<InoToolResult> OpenCard(string cardId);

    Task<InoToolResult> CloseCard(string cardId);

    Task<InoToolResult> AutoArrangeCards();

    Task<InoToolResult> ReportState(ShellStateSnapshot state);

    [DigitalBrain.ReadOnlyCall]
    Task<ShellStateSnapshot> GetState();
}
