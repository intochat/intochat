namespace IntoChat.OS.Modules.UI.Expander;

[GenerateSerializer, Alias("ui.expander.state")]
public sealed record ExpanderState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Body,
    [property: Id(3)] bool IsExpanded,
    [property: Id(4)] bool IsEnabled)
{
    public ExpanderState() : this(0, "", "", false, true) { }
}
