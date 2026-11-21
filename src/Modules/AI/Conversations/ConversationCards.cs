using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Conversations;

internal static class ConversationCards
{
    // Extracts a top-level "card" object (kind, title, optional node JSON) from a tool result payload without reflection.
    public static CardEntry? FromResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetProperty(root, "card", out var card) || card.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var kind = GetString(card, "kind");
            if (string.IsNullOrWhiteSpace(kind))
            {
                return null;
            }

            var title = GetString(card, "title") ?? string.Empty;
            string? nodeJson = null;
            if (TryGetProperty(card, "node", out var nodeProp))
            {
                nodeJson = nodeProp.ValueKind switch
                {
                    JsonValueKind.String => nodeProp.GetString(),
                    JsonValueKind.Object => nodeProp.GetRawText(),
                    _ => null,
                };
            }

            return new CardEntry(kind, title, nodeJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement obj, string camelName, out JsonElement value)
    {
        if (obj.TryGetProperty(camelName, out value))
        {
            return true;
        }
        var pascal = char.ToUpperInvariant(camelName[0]) + camelName[1..];
        return obj.TryGetProperty(pascal, out value);
    }

    private static string? GetString(JsonElement obj, string camelName)
        => TryGetProperty(obj, camelName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
