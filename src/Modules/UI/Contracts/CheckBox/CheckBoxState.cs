namespace IntoChat.OS.Modules.UI.CheckBox;

[GenerateSerializer, Alias("ui.checkbox.state")]
public sealed record CheckBoxState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] bool IsChecked,
    [property: Id(3)] bool IsEnabled)
{
    public CheckBoxState() : this(0, "", false, true) { }
}
