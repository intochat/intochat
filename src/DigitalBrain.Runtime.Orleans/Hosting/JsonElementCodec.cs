using System.Text.Json;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>Orleans wire form of <see cref="JsonElement"/>: its JSON text, or null for an undefined element.</summary>
[GenerateSerializer, Alias("db.json-element")]
internal struct JsonElementSurrogate
{
    [Id(0)]
    public string? Text;
}

[RegisterConverter]
internal sealed class JsonElementSurrogateConverter : IConverter<JsonElement, JsonElementSurrogate>
{
    public JsonElement ConvertFromSurrogate(in JsonElementSurrogate surrogate) => Parse(surrogate.Text);

    public JsonElementSurrogate ConvertToSurrogate(in JsonElement value) => new() { Text = Text(value) };

    internal static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? null : value.GetRawText();

    internal static JsonElement Parse(string? text)
    {
        if (text is null) { return default; }
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
