using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Dialog;

[NeuronAlias("ui.dialog")]
[DefaultNeuronType("ui.dialog")]
public interface IDialog : IUiInteractiveControl
{
    Task<DialogState> Open(
        string title,
        string message,
        string confirmLabel = "Confirm",
        string cancelLabel = "Cancel",
        string tone = "accent",
        string? inputLabel = null,
        string? inputPlaceholder = null,
        string? initialInput = null);

    Task<DialogState> Confirm(string? inputValue = null);
    Task<DialogState> Cancel();

    [ReadOnlyCall]
    Task<DialogState> Read();
}
