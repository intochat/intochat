namespace DigitalBrain.UI.Controls;

/// <summary>The state of a tab strip neuron (WinUI's TabView header strip): only what the user changes.</summary>
public sealed record TabStripState
{
    /// <summary>The selected tab's id; null selects none.</summary>
    [Showcase("""{"kind":"tabStripView","key":"sections","selectedId":"general","props":{"items":[{"id":"general","label":"General","icon":"settings"},{"id":"privacy","label":"Privacy","icon":"lock"},{"id":"about","label":"About"}],"isEnabled":true,"automationName":"Settings sections"}}""")]
    [Showcase("""{"kind":"tabStripView","key":"locked","selectedId":"day","props":{"items":[{"id":"day","label":"Day"},{"id":"week","label":"Week"}],"isEnabled":false,"automationName":"Range"}}""")]
    public string? SelectedId { get; init; }
}

/// <summary>What a tab strip neuron draws: its key and selected id. The client draws the tabs and enabled from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="SelectedId">The selected tab's id; null selects none.</param>
[ViewNode("tabStripView")]
public sealed record TabStripView(string Key, string? SelectedId) : Node;
