using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

[ViewNode("collection")]
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

/// <summary>Display heading rendered in <c>InterDisplay</c>.</summary>
[ViewNode("heading")]
public sealed record Heading(string Value, int Level = 2, string? Subtitle = null) : Node;

/// <summary>Read-only text.</summary>
[ViewNode("text")]
public sealed record Text(string Value) : Node
{
    /// <summary>Semantic tone (<c>neutral</c>, <c>muted</c>, <c>strong</c>, <c>accent</c>, <c>done</c>, <c>working</c>, <c>error</c>).</summary>
    public string Tone { get; init; } = "neutral";

    /// <summary>True to enforce tabular figures (<c>tnum</c>).</summary>
    public bool Tabular { get; init; }

    /// <summary>True for monospace / URI rendering.</summary>
    public bool Mono { get; init; }

    /// <summary>Optional font size override in logical pixels.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Size { get; init; }
}

/// <summary>Compact status badge.</summary>
[ViewNode("badge")]
public sealed record Badge(string Label, string Tone = "neutral", string? Icon = null) : Node;

/// <summary>22px desktop tag pill with tabular figures and optional Lucide icon.</summary>
[ViewNode("tagPill")]
public sealed record TagPill(string Label, string Tone = "neutral", string? Icon = null) : Node;

/// <summary>What an editable field holds; the client picks the input and its keyboard from it.</summary>

/// <param name="Ring">True draws a ring instead of a bar.</param>
[ViewNode("progress")]
public sealed record Progress(
    [Showcase("""{"kind":"progress","label":"Uploading","value":0.42,"detail":"42 of 100 files"}""")]
    [Showcase("""{"kind":"progress","label":"Connecting","value":0,"isIndeterminate":true,"ring":true}""")]
    [Showcase("""{"kind":"progress","label":"Sync failed","value":0.7,"showError":true,"detail":"Stopped at 70%"}""")]
    string Label,
    double Value,
    string? Detail = null,
    bool IsIndeterminate = false,
    bool ShowError = false,
    bool Ring = false) : Node;

/// <summary>An inline status/alert banner: plain data whose close control sends a <see cref="Command"/>.</summary>
/// <param name="Title">The bold lead text.</param>
/// <param name="Message">The body text.</param>
/// <param name="Tone">neutral, accent, success, warning, danger or error.</param>
/// <param name="Action">An optional trailing button.</param>
/// <param name="Close">The command the close control sends; null shows no close control.</param>
/// <param name="Key">The node's key within its view, by which its close names it; required with <paramref name="Close"/>.</param>
/// <param name="AutomationName">The accessible name; empty falls back to <paramref name="Key"/>.</param>
[ViewNode("infoBar")]
public sealed record InfoBar(
    [Showcase("""{"kind":"infoBar","key":"sync-paused","title":"Sync paused","message":"Reconnect storage to resume.","tone":"warning","close":{"target":".","signal":"Dismissed"},"automationName":"Sync paused"}""")]
    [Showcase("""{"kind":"infoBar","key":"saved","title":"Saved","message":"Your changes are stored.","tone":"success","automationName":"Saved"}""")]
    string Title,
    string Message,
    string Tone = "neutral",
    Button? Action = null,
    Command? Close = null,
    string Key = "",
    string AutomationName = "") : Node
{
    // Not serialised (private); a close control is activated by key, so a closable bar has one.
    private readonly bool _keyedClose = Close is null || !string.IsNullOrWhiteSpace(Key)
        ? true
        : throw new ArgumentException("An info bar with a Close command needs a Key.", nameof(Key));

    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = string.IsNullOrEmpty(AutomationName) ? Key : AutomationName;
}

/// <summary>Markdown text (headings, lists, emphasis, links, code): plain data drawn by the client.</summary>
/// <param name="Value">The markdown source.</param>
[ViewNode("markdown")]
public sealed record Markdown(
    [Showcase(@"""## Release notes\n\n- **Faster** sync\n- A new `Card` footer\n\nSee [the docs](https://intochat.io).""")]
    string Value) : Node;

/// <summary>An image drawn by the client from a URL or a <c>data:image/*;base64,...</c> URL. Zoom and rotation are the viewer's own:
/// the client keeps them and never sends them.</summary>
/// <param name="Url">The image URL or data URL; empty draws a "no preview" box.</param>
/// <param name="Caption">A line shown above the image; empty shows none.</param>
/// <param name="Height">The height of the image box in logical pixels.</param>
/// <param name="Fit">How the image fills its box.</param>
[ViewNode("image")]
public sealed record Image(
    [Showcase("""{"kind":"image","url":"data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAxNjAgOTAnPjxyZWN0IHdpZHRoPScxNjAnIGhlaWdodD0nOTAnIGZpbGw9JyM0ZjQ2ZTUnLz48Y2lyY2xlIGN4PSc4MCcgY3k9JzQ1JyByPScyNCcgZmlsbD0nI2ZmZicvPjwvc3ZnPg==","caption":"Design preview","height":180,"fit":"Contain"}""")]
    string Url,
    string Caption = "",
    double Height = 320,
    ImageFit Fit = ImageFit.Contain) : Node;

/// <summary>How an <see cref="Image"/> fills its box.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImageFit>))]
public enum ImageFit
{
    /// <summary>The whole image is visible, letterboxed when its shape differs from the box.</summary>
    Contain,

    /// <summary>The image fills the box and is cropped when its shape differs.</summary>
    Cover,
}

/// <summary>Read-only JSON drawn as a tree the person can expand and filter; both stay in the client.</summary>
/// <param name="Title">The heading above the tree.</param>
/// <param name="Json">The JSON text; text that does not parse is shown as it is.</param>
[ViewNode("jsonTree")]
public sealed record JsonTree(
    [Showcase("""{"kind":"jsonTree","title":"Account state","json":"{\"balance\":42,\"owner\":{\"name\":\"Ada\",\"tags\":[\"admin\",\"ops\"]},\"closed\":false}"}""")]
    string Title,
    string Json) : Node;

/// <summary>A transient message.</summary>
[ViewNode("toast")]
public sealed record Toast(string Message) : Node;

/// <summary>A card frame (the os <c>card</c> control): plain data with a title, a body, child nodes and optional header and footer.</summary>
/// <param name="Title">The card title.</param>
/// <param name="Body">The card body.</param>
/// <param name="Children">Nodes shown below the body, in order.</param>
/// <param name="Header">A node shown above the title, such as a badge row or a child neuron's ref.</param>
/// <param name="Footer">A node shown below the children, such as a row of buttons.</param>
[ViewNode("card")]
public sealed record Card(
    [Showcase("""{"kind":"card","title":"Weekly report","body":"Five tasks closed, two open.","children":[{"kind":"progress","label":"Done","value":0.7}]}""")]
    [Showcase("""{"kind":"card","title":"Deploy","body":"Ready to ship.","children":[],"header":{"kind":"badge","label":"Staging","tone":"accent"},"footer":{"kind":"button","key":"ship","label":"Ship","command":{"target":".","signal":"Shipped"},"tone":"accent","automationName":"Ship"}}""")]
    string Title,
    string Body,
    IReadOnlyList<Node> Children,
    Node? Header = null,
    Node? Footer = null) : Node
{
    private readonly UiStyle? _style;

    /// <summary>Optional visual style authored via <see cref="Mix.Style()"/>.</summary>
    [JsonIgnore]
    public UiStyle? Style
    {
        get => _style;
        init => _style = value;
    }

    /// <summary>Wire representation of <see cref="Style"/>; omitted when unset.</summary>
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StyleWire
    {
        get => _style?.ToWireJsonElement();
        init => _style = value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
            ? UiStyle.FromWireJsonElement(value)
            : null;
    }

    /// <inheritdoc />
    public bool Equals(Card? other) => other is not null && Title == other.Title && Body == other.Body && Children.SequenceEqual(other.Children)
        && Equals(Header, other.Header) && Equals(Footer, other.Footer) && Equals(_style, other._style);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Title, Body, Children.Count, Header, Footer, _style);
}


/// <summary>Semantic tone for status badges and stat cards.</summary>
public enum BadgeTone
{
    /// <summary>Default neutral metadata tone.</summary>
    Neutral = 0,

    /// <summary>Primary brand/accent tone.</summary>
    Accent = 1,

    /// <summary>Completed / healthy green tone.</summary>
    Done = 2,

    /// <summary>In-progress / warning amber tone.</summary>
    Working = 3,

    /// <summary>Destructive / failure red tone.</summary>
    Error = 4,
}

[ViewNode("statCard")]
public sealed record StatCardNode(
    string Key,
    string Label,
    string Value,
    string? Delta = null,
    BadgeTone Tone = BadgeTone.Neutral,
    string? Icon = null,
    string AutomationName = "") : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Label"/> / <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = !string.IsNullOrWhiteSpace(AutomationName) ? AutomationName : !string.IsNullOrWhiteSpace(Label) ? Label : Key ?? "";
}

/// <summary>A conversation timeline, composer, waiting state, error banner, stop button, and empty state.</summary>

/// <summary>Inline recoverable error line with optional detail and retry/recovery button.</summary>
[ViewNode("errorLine")]
public sealed record ErrorLineNode(
    string What,
    string? Detail = null,
    Button? Action = null) : Node;

/// <summary>Empty state prompt with optional body and suggestion buttons.</summary>
[ViewNode("emptyState")]
public sealed record EmptyStateNode(
    string Title,
    string? Body = null,
    IReadOnlyList<Button>? Suggestions = null) : Node
{
    /// <inheritdoc />
    public bool Equals(EmptyStateNode? other) =>
        other is not null
        && Title == other.Title
        && Body == other.Body
        && (ReferenceEquals(Suggestions, other.Suggestions)
            || (Suggestions is not null && other.Suggestions is not null && Suggestions.SequenceEqual(other.Suggestions)));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Title, Body, Suggestions?.Count ?? 0);
}

/// <summary>Micro-app telemetry tile with tabular-numeral metric, optional sparkline series, threshold line, and quick actions.</summary>
[ViewNode("metricTile")]
public sealed record MetricTileNode(
    string Id,
    string AppId,
    string Title,
    string Value,
    string? Delta = null,
    string DeltaTone = "neutral",
    string? StatusLabel = null,
    string StatusTone = "neutral",
    IReadOnlyList<double>? Sparkline = null,
    double? Threshold = null,
    IReadOnlyList<Button>? Actions = null) : Node
{
    /// <inheritdoc />
    public bool Equals(MetricTileNode? other) =>
        other is not null
        && Id == other.Id
        && AppId == other.AppId
        && Title == other.Title
        && Value == other.Value
        && Delta == other.Delta
        && DeltaTone == other.DeltaTone
        && StatusLabel == other.StatusLabel
        && StatusTone == other.StatusTone
        && (ReferenceEquals(Sparkline, other.Sparkline) || (Sparkline is not null && other.Sparkline is not null && Sparkline.SequenceEqual(other.Sparkline)))
        && Nullable.Equals(Threshold, other.Threshold)
        && (ReferenceEquals(Actions, other.Actions) || (Actions is not null && other.Actions is not null && Actions.SequenceEqual(other.Actions)));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, AppId, Title, Value);
}

/// <summary>An installed-app launcher tile emitted by the desktop view.</summary>
[ViewNode("appTile")]
public sealed record AppTileNode(
    [Showcase("""{"kind":"appTile","key":"dock-ino","appId":"ino","title":"Ino","icon":"sparkles","category":"System","launch":0,"dock":0,"appKind":"compiled","command":{"target":".","signal":"desktop.open"},"automationName":"Ino"}""")]
    [Showcase("""{"kind":"appTile","key":"catalog-photocraft","appId":"photocraft","title":"PhotoCraft","icon":"image","category":"Creative","launch":1,"appKind":"compiled","description":"Raster photo studio","command":{"target":".","signal":"desktop.open"},"automationName":"PhotoCraft"}""")]
    string Key,
    string AppId,
    string Title,
    string? Icon = null,
    string? Category = null,
    AppLaunch Launch = AppLaunch.Windowed,
    int? Dock = null,
    string AppKind = "compiled",
    string? Description = null,
    Command? Command = null,
    string AutomationName = "") : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Title"/> / <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = !string.IsNullOrWhiteSpace(AutomationName)
        ? AutomationName
        : !string.IsNullOrWhiteSpace(Title)
            ? Title
            : Key ?? "";
}

/// <summary>Structured code diff preview card for Feature-to-PR workflows.</summary>
