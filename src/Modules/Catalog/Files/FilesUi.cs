using System.Globalization;
using System.Text.Json;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

// The Files views' node vocabulary over DigitalBrain.UI: every button emits to the files app with the revision it was drawn at.
internal static class FilesUi
{
    public const string Primary = ButtonTone.Accent;
    public const string Secondary = ButtonTone.Neutral;
    public const string Ghost = ButtonTone.Neutral;
    public const string Danger = "danger";
    public const string Link = ButtonTone.Neutral;
    public const string Neutral = "neutral";
    public const string Working = "working";

    // A files request as FilesRequests forwards it: the verb, the revision the person saw and the item index it names.
    public static Emit Act(string revision, string verb, int? index = null)
        => new("files", verb == "search-all" ? "search" : verb, JsonSerializer.SerializeToElement(new
        {
            expectedRevision = long.Parse(revision, CultureInfo.InvariantCulture),
            input = new FilesWorkspaceInput(Index: index, Everywhere: verb == "search-all"),
        }, JsonSerializerOptions.Web));

    public static Node Text(string text) => new Text(text);

    public static Node Button(string key, string text, Emit action, bool enabled = true, string style = Secondary, string? icon = null)
        => new Button(key, text, action, Enabled: enabled, Tone: style) { Icon = icon };

    public static Node CopyButton(string key, string label, string text)
        => new Button(key, label, Intent: new Intent(IntentKind.Copy, text)) { Icon = "copy" };

    public static Node Column(Node[] children, double gap = 12, double[]? extents = null) => new Column(children) { Gap = gap, Extents = extents };

    public static Node Row(Node[] children, double gap = 12, double[]? extents = null) => new Row(children) { Gap = gap, Extents = extents };

    public static Node List(Node[] rows) => new Column(rows);

    public static Node Card(string title, string? detail = null, Node[]? children = null) => new Card(title, detail ?? "", children ?? []);

    public static Node Badge(string text, string tone) => new Badge(text, tone);

    public static Node Divider(string? label = null) => new Divider(label);

    public static Node Field(string key, string name, FieldKind kind = FieldKind.Text) => new Field(key, name, kind);

    public static Node Reference(string kind, string name) => new NeuronRef(kind, name);
}
