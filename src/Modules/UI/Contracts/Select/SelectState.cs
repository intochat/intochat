namespace IntoChat.OS.Modules.UI.Select;

[GenerateSerializer, Alias("ui.select.option")]
public sealed record SelectOption(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Subtitle = null)
{
    public SelectOption() : this("", "", null) { }
}

[GenerateSerializer, Alias("ui.select.state")]
public sealed record SelectState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] IReadOnlyList<SelectOption> Options,
    [property: Id(3)] string? SelectedId,
    [property: Id(4)] bool IsEnabled)
{
    public SelectState() : this(0, "", [], null, true) { }
}
