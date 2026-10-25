using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>A screen an app returns: a title and a tree of nodes. Renderer-neutral.</summary>
public sealed record View(string Title, Node Root);

/// <summary>A node of a view.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Column), "column")]
[JsonDerivedType(typeof(Collection), "collection")]
[JsonDerivedType(typeof(Text), "text")]
[JsonDerivedType(typeof(TextField), "textField")]
[JsonDerivedType(typeof(Toast), "toast")]
public abstract record Node;

/// <summary>Children laid out top to bottom.</summary>
public sealed record Column(IReadOnlyList<Node> Children) : Node
{
    /// <inheritdoc />
    public bool Equals(Column? other) => other is not null && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode() => Children.Count;
}

/// <summary>A list of entries.</summary>
public sealed record Collection(IReadOnlyList<Entry> Entries) : Node
{
    /// <inheritdoc />
    public bool Equals(Collection? other) => other is not null && Entries.SequenceEqual(other.Entries);

    /// <inheritdoc />
    public override int GetHashCode() => Entries.Count;
}

/// <summary>One entry of a collection.</summary>
public sealed record Entry(string Id, string Title, string? Subtitle)
{
    /// <summary>Text shown at the end of the entry, such as a size or a quota; null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Trailing { get; init; }
}

/// <summary>Read-only text.</summary>
public sealed record Text(string Value) : Node;

/// <summary>An editable text field.</summary>
public sealed record TextField(string Name, string? Value, string? Placeholder) : Node;

/// <summary>A transient message.</summary>
public sealed record Toast(string Message) : Node;

/// <summary>Where an app publishes what it shows: its side and main views and transient toasts. The host renders the latest of each.</summary>
public interface IPresenter
{
    /// <summary>Replaces the side view, the main view, or both; a null view keeps the current one.</summary>
    void Present(View? side, View? main);

    /// <summary>Shows a transient message.</summary>
    void Toast(Toast toast);
}
