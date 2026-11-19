using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.KanbanBoard.Signals;

namespace IntoChat.OS.Modules.UI.KanbanBoard;

[Neuron("ui.kanban")]
internal sealed class KanbanBoardNeuron : Neuron<KanbanBoardState>, IKanbanBoard
{
    public async Task<KanbanBoardState> Configure(string title, IReadOnlyList<KanbanColumn> columns, IReadOnlyList<KanbanCard> cards, string? selectedCardId = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(cards);

        var cols = columns.ToArray();
        var normalizedCards = NormalizeOrders(cards);
        var next = State with
        {
            Version = State.Version + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "Board" : title.Trim(),
            Columns = cols,
            Cards = normalizedCards,
            SelectedCardId = selectedCardId ?? normalizedCards.FirstOrDefault()?.Id,
        };
        await Save(next, new KanbanBoardChanged(Id.FullKey(), next.Version, next.SelectedCardId));
        return next;
    }

    public async Task<KanbanBoardState> MoveCard(string cardId, string targetColumnId, int targetOrder = 0)
    {
        var column = State.Columns.FirstOrDefault(c => string.Equals(c.Id, targetColumnId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown kanban column '{targetColumnId}'.", nameof(targetColumnId));
        var card = State.Cards.FirstOrDefault(c => string.Equals(c.Id, cardId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown kanban card '{cardId}'.", nameof(cardId));

        if (!string.Equals(card.ColumnId, targetColumnId, StringComparison.Ordinal)
            && column.WipLimit is { } wipLimit
            && State.Cards.Count(c => string.Equals(c.ColumnId, targetColumnId, StringComparison.Ordinal)) >= wipLimit)
        {
            throw new InvalidOperationException($"Column '{column.Title}' has reached its WIP limit ({wipLimit}).");
        }

        var remaining = State.Cards.Where(c => !string.Equals(c.Id, cardId, StringComparison.Ordinal)).ToList();
        var destCards = remaining
            .Where(c => string.Equals(c.ColumnId, targetColumnId, StringComparison.Ordinal))
            .OrderBy(c => c.Order)
            .ToList();

        var insertAt = Math.Clamp(targetOrder, 0, destCards.Count);
        destCards.Insert(insertAt, card with { ColumnId = targetColumnId });

        var rebuilt = new List<KanbanCard>(remaining.Count + 1);
        foreach (var col in State.Columns)
        {
            var colCards = string.Equals(col.Id, targetColumnId, StringComparison.Ordinal)
                ? destCards
                : remaining.Where(c => string.Equals(c.ColumnId, col.Id, StringComparison.Ordinal)).OrderBy(c => c.Order).ToList();
            for (var i = 0; i < colCards.Count; i++)
            {
                rebuilt.Add(colCards[i] with { Order = i });
            }
        }

        var next = State with
        {
            Version = State.Version + 1,
            Cards = [.. rebuilt],
            SelectedCardId = cardId,
        };
        await Save(next, new KanbanBoardChanged(Id.FullKey(), next.Version, next.SelectedCardId, cardId, targetColumnId));
        return next;
    }

    public async Task<KanbanBoardState> UpsertCard(KanbanCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (State.Columns.Length > 0 && State.Columns.All(c => !string.Equals(c.Id, card.ColumnId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown kanban column '{card.ColumnId}'.", nameof(card));
        }

        var list = State.Cards.Where(c => !string.Equals(c.Id, card.Id, StringComparison.Ordinal)).Append(card).ToList();
        var next = State with
        {
            Version = State.Version + 1,
            Cards = NormalizeOrders(list),
            SelectedCardId = card.Id,
        };
        await Save(next, new KanbanBoardChanged(Id.FullKey(), next.Version, next.SelectedCardId));
        return next;
    }

    public async Task<KanbanBoardState> RemoveCard(string cardId)
    {
        var remaining = State.Cards.Where(c => !string.Equals(c.Id, cardId, StringComparison.Ordinal)).ToList();
        if (remaining.Count == State.Cards.Length)
        {
            return State;
        }
        var normalized = NormalizeOrders(remaining);
        var selected = string.Equals(State.SelectedCardId, cardId, StringComparison.Ordinal)
            ? normalized.FirstOrDefault()?.Id
            : State.SelectedCardId;
        var next = State with
        {
            Version = State.Version + 1,
            Cards = normalized,
            SelectedCardId = selected,
        };
        await Save(next, new KanbanBoardChanged(Id.FullKey(), next.Version, next.SelectedCardId));
        return next;
    }

    public async Task<KanbanBoardState> SelectCard(string? cardId)
    {
        if (cardId is not null && State.Cards.All(c => !string.Equals(c.Id, cardId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown kanban card '{cardId}'.", nameof(cardId));
        }
        var next = State with { Version = State.Version + 1, SelectedCardId = cardId };
        await Save(next, new KanbanBoardChanged(Id.FullKey(), next.Version, next.SelectedCardId));
        return next;
    }

    public Task<KanbanBoardState> Read() => Task.FromResult(State);

    private static KanbanCard[] NormalizeOrders(IEnumerable<KanbanCard> cards)
        => cards
            .GroupBy(c => c.ColumnId, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(c => c.Order).Select((c, idx) => c with { Order = idx }))
            .ToArray();
}
