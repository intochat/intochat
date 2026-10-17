using System.Text.Json;

namespace DigitalBrain.Equifax;

// Best-effort over the ASSUMED payload (README): infocorp.negative, sbs.rating, sbs.maxDaysDelay,
// sicom.unpaidDebts, products.mortgage, products.carLoan. A missing or mistyped field is null.
internal static class BureauFlagsReader
{
    public static BureauFlags Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(
                Bool(root, "infocorp", "negative"),
                Text(root, "sbs", "rating"),
                Int(root, "sbs", "maxDaysDelay"),
                Bool(root, "sicom", "unpaidDebts"),
                Bool(root, "products", "mortgage"),
                Bool(root, "products", "carLoan"));
        }
        catch (JsonException) { return new(null, null, null, null, null, null); }
    }

    private static JsonElement? Find(JsonElement root, string section, string field)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty(field, out var value) ? value : null;

    private static bool? Bool(JsonElement root, string section, string field)
        => Find(root, section, field) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    private static string? Text(JsonElement root, string section, string field)
        => Find(root, section, field) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Int(JsonElement root, string section, string field)
        => Find(root, section, field) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;
}
