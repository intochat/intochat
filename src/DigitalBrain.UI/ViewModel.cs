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
public sealed record Entry(string Id, string Title, string? Subtitle);

/// <summary>Read-only text.</summary>
public sealed record Text(string Value) : Node;

/// <summary>An editable text field.</summary>
public sealed record TextField(string Name, string? Value, string? Placeholder) : Node;

/// <summary>A transient message.</summary>
public sealed record Toast(string Message) : Node;
