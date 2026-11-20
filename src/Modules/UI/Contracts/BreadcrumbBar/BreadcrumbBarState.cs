namespace IntoChat.OS.Modules.UI.BreadcrumbBar;

[GenerateSerializer, Alias("ui.breadcrumb.item")]
public sealed record BreadcrumbItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label)
{
    public BreadcrumbItem() : this("", "") { }
}

[GenerateSerializer, Alias("ui.breadcrumb.state")]
public sealed record BreadcrumbBarState(
    [property: Id(0)] int Version,
    [property: Id(1)] BreadcrumbItem[] Items,
    [property: Id(2)] string? ActiveId)
{
    public BreadcrumbBarState() : this(0, [], null) { }
}
