using DigitalBrain;

namespace IntoChat.OS.Modules.UI.KanbanBoard.Signals;

[Signal("ui.kanban-changed")]
public sealed record KanbanBoardChanged(
    string BoardId,
    int Version,
    string? SelectedCardId,
    string? MovedCardId = null,
    string? TargetColumnId = null) : Signal;
