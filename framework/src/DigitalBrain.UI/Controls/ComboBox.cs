namespace DigitalBrain.UI.Controls;

/// <summary>The state of a combo box neuron (WinUI's ComboBox; the legacy <see cref="Select"/> data node is the old way): only what the user changes.</summary>
public sealed record ComboBoxState
{
    /// <summary>The selected option's id; null selects none.</summary>
    [Showcase("""{"kind":"comboBoxView","key":"theme","selectedId":"dark","props":{"header":"Theme","options":[{"id":"light","label":"Light"},{"id":"dark","label":"Dark"},{"id":"system","label":"Use system setting"}],"isEnabled":true,"automationName":"Theme"}}""")]
    [Showcase("""{"kind":"comboBoxView","key":"region","selectedId":"eu","props":{"header":"Data region","options":[{"id":"eu","label":"Europe"},{"id":"us","label":"United States"}],"isEnabled":false,"automationName":"Data region"}}""")]
    public string? SelectedId { get; init; }
}

/// <summary>What a combo box neuron draws: its key and selected id. The client draws the header, options and enabled from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="SelectedId">The selected option's id; null selects none.</param>
[ViewNode("comboBoxView")]
public sealed record ComboBoxView(string Key, string? SelectedId) : Node;
