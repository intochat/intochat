namespace IntoChat.OS.Modules.UI.Toggle;

[GenerateSerializer, Alias("ui.toggle.state")]
public sealed record ToggleState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] bool IsOn,
    [property: Id(3)] bool IsEnabled,
    [property: Id(4)] string OnLabel,
    [property: Id(5)] string OffLabel)
{
    public ToggleState() : this(0, "", false, true, "On", "Off") { }
}
