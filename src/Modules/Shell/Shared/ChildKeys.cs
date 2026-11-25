using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Flutter;

// A child's neuron id never leaves the kernel: a view a client reads names each child by its key in that view (the Dart client
// appends it to the child path it reached the view by and reads the child at GET /brains/{id}/apps/{name}/children/{path}).
internal static class ChildKeys
{
    public static PublishedView? Hide(PublishedView? view) => view is null ? null : view with { View = Hide(view.View) };

    // A screen's view, as a client reads it: children by key, and marked so the client sends every command to the act route.
    public static PublishedView? Screen(PublishedView? view) => view is null ? null : view with { View = Screen(view.View) };

    public static JsonElement Screen(JsonElement view)
    {
        var hidden = Hide(view);
        if (JsonNode.Parse(hidden.GetRawText()) is not JsonObject obj) { return hidden; }
        obj["acts"] = DigitalBrain.UI.View.ActRoute;
        return JsonSerializer.SerializeToElement(obj);
    }

    // Also strips a top-level "acts" mark: only Screen writes it, so a view that reached here carrying one (an app's raw JSON) loses it.
    public static JsonElement Hide(JsonElement view)
    {
        var raw = view.GetRawText();
        if (!raw.Contains("\"" + NeuronRef.ChildKind + "\"", StringComparison.Ordinal) && !raw.Contains("\"acts\"", StringComparison.Ordinal)) { return view; }
        var root = JsonNode.Parse(raw);
        if (root is null) { return view; }
        if (root is JsonObject top) { top.Remove("acts"); }
        Rewrite(root);
        return JsonSerializer.SerializeToElement(root);
    }

    private static void Rewrite(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) { if (item is not null) { Rewrite(item); } }
                break;
            case JsonObject obj:
                if (IsChild(obj)) { obj["neuronUri"] = KeyOf((string?)obj["neuronUri"]); }
                foreach (var (_, value) in obj.ToArray()) { if (value is not null) { Rewrite(value); } }
                break;
        }
    }

    private static bool IsChild(JsonObject obj)
        => obj["kind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["kind"] == "neuronRef"
            && obj["refKind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["refKind"] == NeuronRef.ChildKind
            && obj["neuronUri"]?.GetValueKind() == JsonValueKind.String;

    // Fails closed: a child's id ("<parent id>/<key>") becomes its key; a public address of an app or unit
    // ("<brain>/apps/<name>", "<brain>/units/<alias>") stays, since it names no child; anything else is dropped.
    private static string KeyOf(string? uri)
    {
        if (uri is null || !NeuronId.TryParse(uri, out var id)) { return ""; }
        if (id.Key.LastIndexOf('/') is var slash and > 0) { return id.Key[(slash + 1)..]; }
        return id.Type is "apps" or "units" ? uri : "";
    }
}
