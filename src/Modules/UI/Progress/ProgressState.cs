namespace IntoChat.OS.Modules.UI.Progress;

[GenerateSerializer, Alias("ui.progress.state")]
public sealed record ProgressState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] double Value,
    [property: Id(3)] bool IsIndeterminate,
    [property: Id(4)] string Kind = "bar")
{
    public ProgressState() : this(0, "", 0, false, "bar") { }
}
