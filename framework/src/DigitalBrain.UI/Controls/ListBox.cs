namespace DigitalBrain.UI.Controls;

/// <summary>The state of a list box neuron (WinUI's ListBox / ListView): only what the user changes.</summary>
public sealed record ListBoxState
{
    /// <summary>The selected item's id; null selects none.</summary>
    [Showcase("""{"kind":"listBoxView","key":"devices","selectedId":"laptop","props":{"items":[{"id":"laptop","title":"Laptop","subtitle":"Last seen today"},{"id":"phone","title":"Phone","subtitle":"Last seen yesterday"},{"id":"tablet","title":"Tablet"}],"selectionMode":"Single","isEnabled":true,"automationName":"Devices"}}""")]
    [Showcase("""{"kind":"listBoxView","key":"history","props":{"items":[{"id":"1","title":"Signed in","subtitle":"09:12"},{"id":"2","title":"Key rotated","subtitle":"08:40"}],"selectionMode":"None","isEnabled":true,"automationName":"History"}}""")]
    public string? SelectedId { get; init; }
}

/// <summary>What a list box neuron draws: its key and selected id. The client draws the items, selection mode and enabled from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="SelectedId">The selected item's id; null selects none.</param>
[ViewNode("listBoxView")]
public sealed record ListBoxView(string Key, string? SelectedId) : Node;
