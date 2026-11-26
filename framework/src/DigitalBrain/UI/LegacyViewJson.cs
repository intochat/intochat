using System.Text.Json;
using System.Text.Json.Nodes;

namespace DigitalBrain.UI;

/// <summary>
/// Reads a view stored before every node action was a <see cref="Command"/>: a node's legacy <c>"emit"</c> (<c>{owner, signal, args}</c>)
/// becomes its <c>"command"</c> to the view's own neuron (<c>"."</c>) with the same signal and args, and a legacy <c>"onSubmit"</c> that
/// names an owner instead of a target becomes the same command. The owner is dropped: a view's command only ever reaches the neuron
/// whose view it is (or that neuron's children). A child ref stored as <c>refKind: "slot"</c>, the name before a screen's mounted
/// control was a child, reads as <see cref="NeuronRef.ChildKind"/>. Hosts apply it where they load a stored view, so nothing past the
/// load sees an emit or the retired ref kind.
/// </summary>
public static class LegacyViewJson
{
    // The ref kind a stored view gave a screen's child before it was "child".
    private const string LegacyChildKind = "slot";

    /// <summary><paramref name="view"/> with every legacy emit read as a command and every legacy child ref as a child ref; the same
    /// element when it holds neither.</summary>
    /// <param name="view">A view's wire JSON.</param>
    public static JsonElement Normalize(JsonElement view)
    {
        if (view.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) { return view; }
        var raw = view.GetRawText();
        if (!raw.Contains("\"emit\"", StringComparison.Ordinal) && !raw.Contains("\"owner\"", StringComparison.Ordinal)
            && !raw.Contains("\"" + LegacyChildKind + "\"", StringComparison.Ordinal)) { return view; }
        var root = JsonNode.Parse(raw);
        if (root is null || !Rewrite(root)) { return view; }
        using var normalized = JsonDocument.Parse(root.ToJsonString());
        return normalized.RootElement.Clone();
    }

    // True when anything changed. Args and props are payload, not view: a member named "emit" in them is data.
    private static bool Rewrite(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) { if (item is not null) { changed |= Rewrite(item); } }
                break;
            case JsonObject obj:
                if (obj["kind"] is JsonValue kind && kind.GetValueKind() == JsonValueKind.String && kind.GetValue<string>() == "neuronRef"
                    && obj["refKind"] is JsonValue refKind && refKind.GetValueKind() == JsonValueKind.String && refKind.GetValue<string>() == LegacyChildKind)
                {
                    obj["refKind"] = NeuronRef.ChildKind;
                    changed = true;
                }
                if (obj.ContainsKey("emit"))
                {
                    if (obj["emit"] is JsonObject emit && CommandOf(emit) is { } command && obj["command"] is null) { obj["command"] = command; }
                    obj.Remove("emit");
                    changed = true;
                }
                if (obj["onSubmit"] is JsonObject submit && submit["target"] is null && CommandOf(submit) is { } submitted)
                {
                    obj["onSubmit"] = submitted;
                    changed = true;
                }
                foreach (var (name, value) in obj.ToArray())
                {
                    if (value is not null && name is not ("args" or "props")) { changed |= Rewrite(value); }
                }
                break;
        }
        return changed;
    }

    private static JsonObject? CommandOf(JsonObject emit)
    {
        if (emit["signal"] is not JsonValue signal || signal.GetValueKind() != JsonValueKind.String) { return null; }
        var command = new JsonObject { ["target"] = ".", ["signal"] = signal.GetValue<string>() };
        if (emit["args"] is { } args && args.GetValueKind() != JsonValueKind.Null) { command["args"] = args.DeepClone(); }
        return command;
    }
}
