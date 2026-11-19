namespace DigitalBrain.UI.Controls;

/// <summary>The state of an expander neuron: only what the user changes.</summary>
public sealed record ExpanderState
{
    /// <summary>True shows the content.</summary>
    [Showcase("""{"kind":"expanderView","key":"advanced","isExpanded":true,"props":{"header":"Advanced options","isEnabled":true,"automationName":"Advanced options","children":[{"kind":"text","value":"Shown while expanded."}]}}""")]
    [Showcase("""{"kind":"expanderView","key":"details","isExpanded":false,"props":{"header":"Details","isEnabled":true,"automationName":"Details","children":[{"kind":"text","value":"Hidden until expanded."}]}}""")]
    public bool IsExpanded { get; init; }
}

/// <summary>What an expander neuron draws: its key and whether it is expanded. The client draws the header, content and enabled from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="IsExpanded">True shows the content.</param>
[ViewNode("expanderView")]
public sealed record ExpanderView(string Key, bool IsExpanded) : Node;
