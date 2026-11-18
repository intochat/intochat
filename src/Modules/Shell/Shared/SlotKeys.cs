using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Flutter;

// A slot child's neuron id never leaves the kernel: a view a client reads names each slot by its key in that view (the Dart client
// appends it to the slot path it reached the view by and reads the child at GET /brains/{id}/apps/{name}/slots/{path}).
internal static class SlotKeys
{
    public static PublishedView? Hide(PublishedView? view) => view is null ? null : view with { View = Hide(view.View) };

    public static JsonElement Hide(JsonElement view)
    {
        if (!view.GetRawText().Contains("\"slot\"", StringComparison.Ordinal)) { return view; }
        var root = JsonNode.Parse(view.GetRawText());
        if (root is null) { return view; }
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
                if (KeyOf(obj) is { } key) { obj["neuronUri"] = key; }
                foreach (var (_, value) in obj.ToArray()) { if (value is not null) { Rewrite(value); } }
                break;
        }
    }

    // The slot's key: the last segment of the child's neuron key (the parent's id, '/', the key).
    private static string? KeyOf(JsonObject obj)
        => obj["kind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["kind"] == "neuronRef"
            && obj["refKind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["refKind"] == "slot"
            && obj["neuronUri"]?.GetValueKind() == JsonValueKind.String && NeuronId.TryParse((string?)obj["neuronUri"], out var child)
            && child.Key.LastIndexOf('/') is var slash and > 0
                ? child.Key[(slash + 1)..]
                : null;
}
