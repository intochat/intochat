using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.KanbanBoard;

[GenerateSerializer, Alias("ui.kanban.column")]
public sealed record KanbanColumn(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Icon = null,
    [property: Id(3)] int? WipLimit = null)
{
    public KanbanColumn() : this("", "", null, null) { }
}

[GenerateSerializer, Alias("ui.kanban.card")]
public sealed record KanbanCard(
    [property: Id(0)] string Id,
    [property: Id(1)] string ColumnId,
    [property: Id(2)] string Title,
    [property: Id(3)] string? Subtitle = null,
    [property: Id(4)] string? Tag = null,
    [property: Id(5)] string? Assignee = null,
    [property: Id(6)] int Order = 0)
{
    public KanbanCard() : this("", "", "", null, null, null, 0) { }
}

[GenerateSerializer, Alias("ui.kanban.state")]
public sealed record KanbanBoardState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] KanbanColumn[] Columns,
    [property: Id(3)] KanbanCard[] Cards,
    [property: Id(4)] string? SelectedCardId)
{
    public KanbanBoardState() : this(0, "Board", [], [], null) { }
}

[NeuronAlias("ui.kanban")]
[DefaultNeuronType("ui.kanban")]
public interface IKanbanBoard
{
    Task<KanbanBoardState> Configure(string title, IReadOnlyList<KanbanColumn> columns, IReadOnlyList<KanbanCard> cards, string? selectedCardId = null);
    Task<KanbanBoardState> MoveCard(string cardId, string targetColumnId, int targetOrder = 0);
    Task<KanbanBoardState> UpsertCard(KanbanCard card);
    Task<KanbanBoardState> RemoveCard(string cardId);
    Task<KanbanBoardState> SelectCard(string? cardId);
    [ReadOnlyCall] Task<KanbanBoardState> Read();
}
