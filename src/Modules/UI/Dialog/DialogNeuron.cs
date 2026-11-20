using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Dialog.Signals;

namespace IntoChat.OS.Modules.UI.Dialog;

[Neuron("ui.dialog")]
internal sealed class DialogNeuron : Neuron<DialogState>, IDialog
{
    public async Task<DialogState> Open(
        string title,
        string message,
        string confirmLabel = "Confirm",
        string cancelLabel = "Cancel",
        string tone = "accent",
        string? inputLabel = null,
        string? inputPlaceholder = null,
        string? initialInput = null)
    {
        var next = State with
        {
            Version = State.Version + 1,
            IsOpen = true,
            Title = title ?? "",
            Message = message ?? "",
            ConfirmLabel = string.IsNullOrWhiteSpace(confirmLabel) ? "Confirm" : confirmLabel,
            CancelLabel = string.IsNullOrWhiteSpace(cancelLabel) ? "Cancel" : cancelLabel,
            Tone = string.IsNullOrWhiteSpace(tone) ? "accent" : tone,
            InputLabel = inputLabel,
            InputPlaceholder = inputPlaceholder,
            InputValue = initialInput,
        };
        await Save(next, new DialogChanged(Id.FullKey(), next.Version, next.IsOpen));
        return next;
    }

    public async Task<DialogState> Confirm(string? inputValue = null)
    {
        if (!State.IsOpen) { return State; }
        var next = State with
        {
            Version = State.Version + 1,
            IsOpen = false,
            InputValue = inputValue ?? State.InputValue,
        };
        await Save(next, new DialogChanged(Id.FullKey(), next.Version, false), new DialogResolved(Id.FullKey(), next.Version, Confirmed: true, next.InputValue));
        return next;
    }

    public async Task<DialogState> Cancel()
    {
        if (!State.IsOpen) { return State; }
        var next = State with { Version = State.Version + 1, IsOpen = false };
        await Save(next, new DialogChanged(Id.FullKey(), next.Version, false), new DialogResolved(Id.FullKey(), next.Version, Confirmed: false, null));
        return next;
    }

    public Task<DialogState> Read() => Task.FromResult(State);
}
