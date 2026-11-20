using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.GridView;

[GenerateSerializer, Alias("ui.grid-view.item")]
public sealed record GridViewItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Subtitle = null,
    [property: Id(3)] string? Icon = null,
    [property: Id(4)] string? ThumbnailUrl = null,
    [property: Id(5)] string? Badge = null)
{
    public GridViewItem() : this("", "", null, null, null, null) { }
}

[GenerateSerializer, Alias("ui.grid-view.state")]
public sealed record GridViewState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] GridViewItem[] Items,
    [property: Id(3)] string? SelectedId,
    [property: Id(4)] int Columns)
{
    public GridViewState() : this(0, "Grid", [], null, 3) { }
}

[NeuronAlias("ui.grid-view")]
[DefaultNeuronType("ui.grid-view")]
public interface IGridView : IUiInteractiveControl
{
    Task<GridViewState> Configure(string title, IReadOnlyList<GridViewItem> items, string? selectedId = null, int columns = 3);
    Task<GridViewState> Select(string itemId);
    [ReadOnlyCall] Task<GridViewState> Read();
}
