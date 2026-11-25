/*
# Kanban

A control neuron: a board of columns holding cards. Its title, columns and cards are props its parent writes on the slot; the board never
stores a card. MoveCard only signals CardMoved, and the parent moves the card in the props it renders next, so the board shows a move
only once the parent took it. The parent refuses a move naming a card or a column its props do not hold (control.unknownCard,
control.unknownColumn), a position past the column's other cards (control.indexOutOfRange) and a move from another column into a full
column (control.columnFull); a refused move never reaches the board and the board stays drawn as it was.

The board owns the selected card: Select stores it and signals SelectionChanged, or, when the parent owns the selection (props
SelectedCardId), only signals it, every time. The parent refuses a Select naming no card of its props (control.unknownItem).
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A kanban board: it owns the selected card and signals <see cref="SelectionChanged"/> and <see cref="CardMoved"/>.</summary>
[Neuron("control.kanban"), Publishes(typeof(SelectionChanged)), Publishes(typeof(CardMoved))]
public sealed class KanbanNeuron : ControlNeuron<KanbanState, KanbanProps>, IKanbanControl
{
    [On]
    public Task Select(DigitalBrain.UI.Commands.Select command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        if (Props?.SelectedCardId is not null) { return Publish(new SelectionChanged(command.Id)); }
        return command.Id == State.SelectedCardId ? Task.CompletedTask : Show(State with { SelectedCardId = command.Id }, new SelectionChanged(command.Id));
    }

    [On]
    public Task MoveCard(MoveCard command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ColumnId);
        ArgumentOutOfRangeException.ThrowIfNegative(command.Index, nameof(command));
        return Publish(new CardMoved(command.CardId, command.ColumnId, command.Index));
    }

    protected override View Render(KanbanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new KanbanView(Key, state.SelectedCardId));
    }
}
