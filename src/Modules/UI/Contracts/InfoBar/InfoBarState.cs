namespace IntoChat.OS.Modules.UI.InfoBar;

[GenerateSerializer, Alias("ui.info-bar.state")]
public sealed record InfoBarState(
    [property: Id(0)] int Version,
    [property: Id(1)] bool IsOpen,
    [property: Id(2)] string Title,
    [property: Id(3)] string Message,
    [property: Id(4)] string Severity,
    [property: Id(5)] bool IsClosable)
{
    public InfoBarState() : this(0, false, "", "", "info", true) { }
}
