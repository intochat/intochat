using System.Security.Cryptography;
using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

internal sealed record DigestEnvelope(AppManifest Manifest, string Spec, Dictionary<string, string> Files);

internal static class AppPublicationDigest
{
    public static string Of(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("intochat-os-release-v1");
            Write(writer, DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new DigestEnvelope(bundle.Manifest, bundle.Spec, bundle.Files), typeof(DigestEnvelope)));
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) { Write(writer, item); }
            writer.WriteEndArray();
        }
        else { value.WriteTo(writer); }
    }
}
