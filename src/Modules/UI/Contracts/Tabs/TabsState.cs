namespace IntoChat.OS.Modules.UI.Tabs;

[GenerateSerializer, Alias("ui.tabs.item")]
public sealed record UiTabItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Icon = null)
{
    public UiTabItem() : this("", "", null) { }
}

[GenerateSerializer, Alias("ui.tabs.state")]
public sealed record TabsState(
    [property: Id(0)] int Version,
    [property: Id(1)] UiTabItem[] Items,
    [property: Id(2)] string? SelectedId,
    [property: Id(3)] bool IsEnabled)
{
    public TabsState() : this(0, [], null, true) { }
}
