using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;
/// <summary>Children laid out top to bottom.</summary>
[ViewNode("column")]
public sealed record Column(IReadOnlyList<Node> Children) : Node
{
    /// <summary>Vertical spacing between children in logical pixels.</summary>
    public double Gap { get; init; } = 12;

    /// <summary>Optional inner padding in logical pixels.</summary>
    public double Padding { get; init; }

    /// <summary>Optional fixed/flex extents per child.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? Extents { get; init; }

    /// <inheritdoc />
    public bool Equals(Column? other) => other is not null && Children.SequenceEqual(other.Children) && Gap.Equals(other.Gap) && Padding.Equals(other.Padding) && ((Extents is null && other.Extents is null) || (Extents is not null && other.Extents is not null && Extents.SequenceEqual(other.Extents)));

    /// <inheritdoc />
    public override int GetHashCode() => Children.Count;
}

/// <summary>Children laid out left to right.</summary>
[ViewNode("row")]
public sealed record Row(IReadOnlyList<Node> Children) : Node
{
    /// <summary>Horizontal spacing between children in logical pixels.</summary>
    public double Gap { get; init; } = 12;

    /// <summary>Optional inner padding in logical pixels.</summary>
    public double Padding { get; init; }

    /// <summary>Optional fixed/flex extents per child.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? Extents { get; init; }

    /// <inheritdoc />
    public bool Equals(Row? other) => other is not null && Children.SequenceEqual(other.Children) && Gap.Equals(other.Gap) && Padding.Equals(other.Padding) && ((Extents is null && other.Extents is null) || (Extents is not null && other.Extents is not null && Extents.SequenceEqual(other.Extents)));

    /// <inheritdoc />
    public override int GetHashCode() => Children.Count;
}

/// <summary>Children arranged in a multi-column grid.</summary>
[ViewNode("grid")]
public sealed record Grid(int Columns, IReadOnlyList<Node> Children, double Gap = 12) : Node
{
    /// <inheritdoc />
    public bool Equals(Grid? other) => other is not null && Columns == other.Columns && Gap.Equals(other.Gap) && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Columns, Children.Count);
}

/// <summary>Two-pane desktop split view.</summary>
[ViewNode("splitPane")]
public sealed record SplitPane(Node Leading, Node Trailing, double LeadingExtent = 280, double Gap = 16) : Node;

/// <summary>Known surface materiality kinds.</summary>
public static class SurfaceKind
{
    /// <summary>Frosted glass / acrylic surface.</summary>
    public const string Acrylic = "acrylic";

    /// <summary>Elevated card surface.</summary>
    public const string Elevated = "elevated";

    /// <summary>Recessed / sunk well surface.</summary>
    public const string Recessed = "recessed";

    /// <summary>Subtle inset / quiet well surface.</summary>
    public const string Subtle = "subtle";
}

/// <summary>A glassmorphic, elevated, or recessed container surface.</summary>
[ViewNode("surface")]
public sealed record Surface(
    IReadOnlyList<Node> Children,
    [property: JsonPropertyName("surfaceKind")] string Kind = SurfaceKind.Acrylic,
    string? Title = null,
    string? Subtitle = null,
    double Padding = 18) : Node
{
    /// <inheritdoc />
    public bool Equals(Surface? other) => other is not null && Kind == other.Kind && Title == other.Title && Subtitle == other.Subtitle && Padding.Equals(other.Padding) && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Title, Children.Count);
}

/// <summary>A hairline section divider with an optional label.</summary>
[ViewNode("divider")]
public sealed record Divider(string? Label = null) : Node;

/// <summary>Fixed empty space between elements.</summary>
[ViewNode("spacer")]
public sealed record Spacer(double Size = 12) : Node;

/// <summary>A list of entries.</summary>

/// <summary>Studio-grade desktop application shell with optional Acrylic sidebar, top toolbar, main body, and right-hand inspector.</summary>
[ViewNode("appShell")]
public sealed record AppShellNode(
    string Key,
    Node? Sidebar,
    Node Body,
    Node? Toolbar = null,
    Node? Inspector = null,
    string AutomationName = "",
    string Transition = "",
    bool EdgeToEdge = false) : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = !string.IsNullOrWhiteSpace(AutomationName) ? AutomationName : Key ?? "";
}

/// <summary>Navigation rail / sidebar item with icon, active indicator, and optional count/status badge.</summary>
[ViewNode("navItem")]
public sealed record NavItemNode(
    string Key,
    string Label,
    string Icon,
    string Action,
    bool Selected = false,
    string? Badge = null,
    Command? Command = null,
    string AutomationName = "") : Node
{
    // Reads stored views and fixtures written before nav items carried commands.
    [JsonConstructor]
    private NavItemNode(string key, string label, string icon, string action, bool selected, string? badge, Command? command, string automationName, Emit? emit)
        : this(key, label, icon, action, selected, badge, command ?? (emit is null ? null : new Command(".", emit.Signal, emit.Args)), automationName ?? "")
    {
    }

    [JsonInclude, JsonPropertyName("emit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
#pragma warning disable CA1822
    private Emit? Emit => null;
#pragma warning restore CA1822

    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = string.IsNullOrEmpty(AutomationName) ? Key : AutomationName;
}

/// <summary>Compact KPI / telemetry card with tabular-numeral (<c>tnum</c>) metric value and optional delta pill.</summary>
