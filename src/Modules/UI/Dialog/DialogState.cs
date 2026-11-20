namespace IntoChat.OS.Modules.UI.Dialog;

[GenerateSerializer, Alias("ui.dialog.state")]
public sealed record DialogState(
    [property: Id(0)] int Version,
    [property: Id(1)] bool IsOpen,
    [property: Id(2)] string Title,
    [property: Id(3)] string Message,
    [property: Id(4)] string ConfirmLabel,
    [property: Id(5)] string CancelLabel,
    [property: Id(6)] string Tone,
    [property: Id(7)] string? InputLabel,
    [property: Id(8)] string? InputPlaceholder,
    [property: Id(9)] string? InputValue)
{
    public DialogState() : this(0, false, "", "", "Confirm", "Cancel", "accent", null, null, null) { }
}
