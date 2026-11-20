namespace IntoChat.OS.Modules.UI.ColorPicker;

[GenerateSerializer, Alias("ui.color-picker.state")]
public sealed record ColorPickerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] string Hex,
    [property: Id(3)] string[] Swatches,
    [property: Id(4)] bool IsEnabled)
{
    public static readonly IReadOnlyList<string> DefaultSwatches =
        ["#1F5EFF", "#0078D4", "#5E6AD2", "#10B981", "#06B6D4", "#F59E0B", "#EF4444", "#8B5CF6"];

    public ColorPickerState() : this(0, "", "#1F5EFF", [.. DefaultSwatches], true) { }
}
