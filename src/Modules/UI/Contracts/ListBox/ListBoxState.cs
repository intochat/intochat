namespace IntoChat.OS.Modules.UI.ListBox;

[GenerateSerializer, Alias("ui.listbox.item")]
public sealed record ListBoxItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Subtitle = null,
    [property: Id(3)] string? Group = null,
    [property: Id(4)] string? Icon = null)
{
    public ListBoxItem() : this("", "", null, null, null) { }
}

[GenerateSerializer, Alias("ui.listbox.state")]
public sealed record ListBoxState(
    [property: Id(0)] int Version,
    [property: Id(1)] IReadOnlyList<ListBoxItem> Items,
    [property: Id(2)] string? SelectedId,
    [property: Id(3)] bool IsEnabled)
{
    public ListBoxState() : this(0, [], null, true) { }
}
