using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>Fluent container builder supporting both variadic construction and C# indexer child syntax (<c>VStack(...)[ a, b ]</c>).</summary>
public sealed class StackBuilder : KitNode
{
    private readonly bool _vertical;
    private double _gap;
    private double _padding;
    private IReadOnlyList<double>? _extents;
    private IReadOnlyList<Node> _children;

    internal StackBuilder(bool vertical, double gap, double padding, IReadOnlyList<Node?> children)
    {
        _vertical = vertical;
        _gap = gap;
        _padding = padding;
        _children = FilterNulls(children);
    }

    public StackBuilder this[params Node?[] children]
    {
        get
        {
            _children = FilterNulls(children);
            return this;
        }
    }

    public StackBuilder Gap(double gap)
    {
        _gap = gap;
        return this;
    }

    public StackBuilder Padding(double padding)
    {
        _padding = padding;
        return this;
    }

    public StackBuilder Extents(params double[] extents)
    {
        _extents = extents;
        return this;
    }

    public override Node ToNode() => _vertical
        ? new Column(_children) { Gap = _gap, Padding = _padding, Extents = _extents }
        : new Row(_children) { Gap = _gap, Padding = _padding, Extents = _extents };

    internal static IReadOnlyList<Node> FilterNulls(IEnumerable<Node?>? items)
        => items is null ? [] : [.. items.OfType<Node>()];
}

/// <summary>Fluent multi-column grid builder supporting <c>Grid(cols: 3)[ ... ]</c>.</summary>
public sealed class GridBuilder : KitNode
{
    private readonly int _columns;
    private double _gap;
    private IReadOnlyList<Node> _children;

    internal GridBuilder(int columns, double gap, IReadOnlyList<Node?> children)
    {
        _columns = Math.Max(1, columns);
        _gap = gap;
        _children = StackBuilder.FilterNulls(children);
    }

    public GridBuilder this[params Node?[] children]
    {
        get
        {
            _children = StackBuilder.FilterNulls(children);
            return this;
        }
    }

    public GridBuilder Gap(double gap)
    {
        _gap = gap;
        return this;
    }

    public override Node ToNode() => new Grid(_columns, _children, _gap);
}

/// <summary>Fluent card builder supporting <c>Card("Title", "Subtitle")[ ... ]</c>.</summary>
public sealed class CardBuilder : KitNode
{
    private readonly string _title;
    private readonly string _body;
    private IReadOnlyList<Node> _children;
    private Node? _header;
    private Node? _footer;

    internal CardBuilder(string title, string body, IReadOnlyList<Node?> children)
    {
        _title = title ?? string.Empty;
        _body = body ?? string.Empty;
        _children = StackBuilder.FilterNulls(children);
    }

    public CardBuilder this[params Node?[] children]
    {
        get
        {
            _children = StackBuilder.FilterNulls(children);
            return this;
        }
    }

    /// <summary>Sets the node shown above the title.</summary>
    public CardBuilder Header(Node? header)
    {
        _header = header;
        return this;
    }

    /// <summary>Sets the node shown below the children, such as a row of buttons.</summary>
    public CardBuilder Footer(Node? footer)
    {
        _footer = footer;
        return this;
    }

    public override Node ToNode() => new Card(_title, _body, _children, _header, _footer);
}

/// <summary>Fluent materiality surface builder (<c>acrylic</c>, <c>elevated</c>, <c>recessed</c>, <c>subtle</c>).</summary>
public sealed class SurfaceBuilder : KitNode
{
    private readonly string _kind;
    private string? _title;
    private string? _subtitle;
    private double _padding;
    private IReadOnlyList<Node> _children;

    internal SurfaceBuilder(string kind, string? title, string? subtitle, double padding, IReadOnlyList<Node?> children)
    {
        _kind = kind;
        _title = title;
        _subtitle = subtitle;
        _padding = padding;
        _children = StackBuilder.FilterNulls(children);
    }

    public SurfaceBuilder this[params Node?[] children]
    {
        get
        {
            _children = StackBuilder.FilterNulls(children);
            return this;
        }
    }

    public SurfaceBuilder Title(string title, string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle ?? _subtitle;
        return this;
    }

    public SurfaceBuilder Padding(double padding)
    {
        _padding = padding;
        return this;
    }

    public override Node ToNode() => new Surface(_children, _kind, _title, _subtitle, _padding);
}

/// <summary>Fluent text builder supporting semantic tones, monospace, tabular figures, and explicit font size.</summary>
public sealed class TextBuilder : KitNode
{
    private readonly string _value;
    private string _tone;
    private bool _tabular;
    private bool _mono;
    private double? _size;

    internal TextBuilder(string value, string tone = "neutral")
    {
        _value = value ?? string.Empty;
        _tone = tone;
    }

    public TextBuilder Tone(string tone)
    {
        _tone = tone;
        return this;
    }

    public TextBuilder Muted()
    {
        _tone = "muted";
        return this;
    }

    public TextBuilder Strong()
    {
        _tone = "strong";
        return this;
    }

    public TextBuilder Tabular(bool tabular = true)
    {
        _tabular = tabular;
        return this;
    }

    public TextBuilder Mono(bool mono = true)
    {
        _mono = mono;
        return this;
    }

    public TextBuilder Size(double size)
    {
        _size = size;
        return this;
    }

    public override Node ToNode() => new Text(_value)
    {
        Tone = _tone,
        Tabular = _tabular,
        Mono = _mono,
        Size = _size,
    };
}
