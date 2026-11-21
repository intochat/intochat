using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Ino;

// Extracts InoCards from tool call JSON results and inline live Slot(neuronUri) attachments from assistant prose.
internal static partial class InoCards
{
    public static InoCard[] FromToolCalls(IEnumerable<AgentToolCall> calls)
        => [.. calls.Select(c => c.Result is { Length: > 0 } r ? FromResult(r) : null).OfType<InoCard>()];

    public static (string CleanText, IReadOnlyList<NeuronRef> Slots) ExtractInlineSlots(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText) || !rawText.Contains("slot:", StringComparison.OrdinalIgnoreCase))
        {
            return (rawText ?? string.Empty, []);
        }

        var slots = new List<NeuronRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Capture(Match m)
        {
            if (ToLiveSlot(m.Groups["target"].Value) is { } s && seen.Add($"{s.Kind}:{s.NeuronUri}")) { slots.Add(s); }
            return string.Empty;
        }

        var cleaned = UriSlotRegex().Replace(BracketSlotRegex().Replace(rawText, Capture), Capture);
        return slots.Count == 0 ? (rawText, []) : (cleaned.Trim(), slots);
    }

    public static NeuronRef? ToLiveSlot(string? rawTarget, string? explicitKind = null)
    {
        if (string.IsNullOrWhiteSpace(rawTarget)) { return null; }
        var trimmed = rawTarget.Trim().TrimEnd('.', ',', ';', ')');
        if (trimmed.StartsWith("slot://", StringComparison.OrdinalIgnoreCase)) { trimmed = trimmed[7..]; }

        var refKind = explicitKind;
        foreach (var (prefix, kind) in (ReadOnlySpan<(string, string)>)[("app:", "app"), ("unit:", "unit"), ("view:", "view")])
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                refKind ??= kind;
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        var uri = InoView.StripNeuronUri(trimmed).Trim('/');
        if (uri.IndexOf('#') is > 0 and var hash) { uri = uri[..hash]; }
        if (string.IsNullOrWhiteSpace(uri)) { return null; }

        var resolved = !string.IsNullOrWhiteSpace(refKind) ? refKind
            : uri.StartsWith("unit.", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("units/", StringComparison.OrdinalIgnoreCase) ? "unit"
            : !uri.Contains('/', StringComparison.Ordinal) && !uri.Contains('.', StringComparison.Ordinal) ? "app"
            : "view";
        if (resolved == "view" && uri.StartsWith("apps/", StringComparison.OrdinalIgnoreCase) && !uri.EndsWith("/view", StringComparison.OrdinalIgnoreCase))
        {
            uri = $"{uri}/view";
        }

        return Slot(uri, resolved);
    }

    internal static InoCard? FromResult(string result)
    {
        try
        {
            using var payload = JsonDocument.Parse(result);
            return payload.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                ? card.Deserialize(InoJson.Default.InoCard)
                : null;
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"\[slot:(?<target>[^\]\s]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketSlotRegex();

    [GeneratedRegex(@"(?<!\w)slot://(?<target>[^\s\]\)"">]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriSlotRegex();
}
