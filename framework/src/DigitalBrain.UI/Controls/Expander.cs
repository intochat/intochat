namespace DigitalBrain.UI.Controls;

/// <summary>The state of an expander neuron.</summary>
public sealed record ExpanderState
{
    /// <summary>The always-visible header.</summary>
    [Showcase("""{"kind":"expanderView","key":"advanced","header":"Advanced options","isExpanded":true,"automationName":"Advanced options","children":[{"kind":"text","value":"Shown while expanded."}]}""")]
    [Showcase("""{"kind":"expanderView","key":"details","header":"Details","isExpanded":false,"automationName":"Details","children":[{"kind":"text","value":"Hidden until expanded."}]}""")]
    public string Header { get; init; } = "";

    /// <summary>True shows <see cref="Children"/>.</summary>
    public bool IsExpanded { get; init; }

    /// <summary>The content shown while expanded: data nodes, or slots (<see cref="NeuronRef"/>) of child neurons.</summary>
    public Node[] Children { get; init; } = [];

    /// <summary>The accessible name; null falls back to the neuron's key.</summary>
    public string? AutomationName { get; init; }

    /// <inheritdoc />
    public bool Equals(ExpanderState? other) => other is not null && Header == other.Header && IsExpanded == other.IsExpanded
        && Children.SequenceEqual(other.Children) && AutomationName == other.AutomationName;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Header, IsExpanded, Children.Length, AutomationName);
}

/// <summary>What an expander neuron draws: plain data; the client sends its commands to the neuron (".").</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Header">The always-visible header.</param>
/// <param name="IsExpanded">True shows <paramref name="Children"/>.</param>
/// <param name="AutomationName">The accessible name.</param>
/// <param name="Children">The content shown while expanded.</param>
[ViewNode("expanderView")]
public sealed record ExpanderView(string Key, string Header, bool IsExpanded, string AutomationName, IReadOnlyList<Node> Children) : Node
{
    /// <inheritdoc />
    public bool Equals(ExpanderView? other) => other is not null && Key == other.Key && Header == other.Header && IsExpanded == other.IsExpanded
        && AutomationName == other.AutomationName && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Header, IsExpanded, Children.Count);
}
