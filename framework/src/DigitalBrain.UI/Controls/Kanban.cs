namespace DigitalBrain.UI.Controls;

/// <summary>The state of a kanban neuron: only what the user changes. Its columns and cards are the parent's props and never stored here.</summary>
public sealed record KanbanState
{
    /// <summary>The selected card's id; null selects none. Stays null while the parent owns the selection.</summary>
    [Showcase("""{"kind":"kanbanView","key":"work","selectedCardId":"ship","props":{"title":"Release","columns":[{"id":"todo","title":"To do"},{"id":"doing","title":"Doing","wipLimit":2},{"id":"done","title":"Done","icon":"check"}],"cards":[{"id":"write","columnId":"todo","title":"Write notes","tag":"docs"},{"id":"ship","columnId":"doing","title":"Ship 2.0","subtitle":"Due Friday","assignee":"Ada"},{"id":"plan","columnId":"done","title":"Plan"}],"isEnabled":true,"automationName":"Release board"}}""")]
    [Showcase("""{"kind":"kanbanView","key":"locked","selectedCardId":null,"props":{"title":"Archive","columns":[{"id":"done","title":"Done"}],"cards":[{"id":"old","columnId":"done","title":"Old work"}],"isEnabled":false,"automationName":"Archive board"}}""")]
    public string? SelectedCardId { get; init; }
}

/// <summary>What a kanban neuron draws: its key and selected card. The client draws the title, columns and cards from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="SelectedCardId">The board's own selected card; null when none is selected or the parent owns the selection.</param>
[ViewNode("kanbanView")]
public sealed record KanbanView(string Key, string? SelectedCardId) : Node;
