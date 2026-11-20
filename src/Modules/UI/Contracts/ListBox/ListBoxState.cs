namespace IntoChat.OS.Modules.UI.ListBox;

[GenerateSerializer, Alias("ui.listbox.item")]
public sealed record UiListBoxItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Subtitle = null,
    [property: Id(3)] string? Group = null,
    [property: Id(4)] string? Icon = null)
{
    public UiListBoxItem() : this("", "", null, null, null) { }
}

[GenerateSerializer, Alias("ui.listbox.state")]
public sealed record UiListBoxState(
    [property: Id(0)] int Version,
    [property: Id(1)] UiListBoxItem[] Items,
    [property: Id(2)] string? SelectedId,
    [property: Id(3)] bool IsEnabled)
{
    public UiListBoxState() : this(0, [], null, true) { }
}
