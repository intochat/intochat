using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices;

[GenerateSerializer, Alias("devices.runtime-id"), JsonConverter(typeof(RuntimeIdJsonConverter))]
public readonly record struct RuntimeId([property: Id(0)] string Value)
{
    public static readonly RuntimeId Ollama = new("ollama");
    public static readonly RuntimeId FoundryLocal = new("foundry-local");
    public static readonly RuntimeId SherpaOnnx = new("sherpa-onnx");

    public static RuntimeId OpenAiCompat(string name) => new($"openai-compat:{name}");

    public override string ToString() => Value ?? string.Empty;
}

public sealed class RuntimeIdJsonConverter : JsonConverter<RuntimeId>
{
    public override RuntimeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, RuntimeId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value ?? string.Empty);

    public override RuntimeId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, RuntimeId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value ?? string.Empty);
}
