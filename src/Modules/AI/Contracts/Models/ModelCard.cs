using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.Models;

[GenerateSerializer, Alias("ai.model-id"), JsonConverter(typeof(ModelIdJsonConverter))]
public readonly record struct ModelId([property: Id(0)] string Value)
{
    public override string ToString() => Value ?? string.Empty;
}

public sealed class ModelIdJsonConverter : JsonConverter<ModelId>
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value ?? string.Empty);

    public override ModelId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value ?? string.Empty);
}

[JsonConverter(typeof(JsonStringEnumConverter<Where>))]
public enum Where
{
    Device,
    Server,
    Cloud,
}

[Flags, JsonConverter(typeof(JsonStringEnumConverter<Abilities>))]
public enum Abilities
{
    None = 0,
    Text = 1,
    Tools = 2,
    Vision = 4,
    Reasoning = 8,
    StructuredOutput = 16,
    Hear = 32,
    Speak = 64,
    Talk = 128,
    Embed = 256,
    Image = 512,
}

[JsonConverter(typeof(JsonStringEnumConverter<Tier>))]
public enum Tier
{
    Fast,
    Balanced,
    Deep,
}

public sealed record ModelPrice(
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal? CachedInputPerMTok = null,
    decimal? ReasoningPerMTok = null);

public sealed record ModelCard(
    ModelId Id,
    AiProvider Provider,
    string Family,
    string Name,
    Where Where,
    Abilities Can,
    Tier Tier,
    ModelPrice? Price = null,
    long? Bytes = null);

public static class ModelCards
{
    public static string IconKey(this ModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.Provider switch
        {
            AiProvider.FoundryLocal => "provider:foundry",
            AiProvider.TypeSafe or AiProvider.Host => "brand:intochat",
            _ => "provider:" + card.Provider.ToString().ToLowerInvariant(),
        };
    }
}
