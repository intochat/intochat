using System.Buffers;
using System.Text.Json;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Mcp;

// A tool's text result as JSON: parsed when it is JSON, otherwise wrapped as {"result": text}.
internal static class ToolResultJson
{
    public static JsonElement From(string text, ILogger log)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            log.LogSwallowed(error, "Wrapping non-JSON tool result text.");
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("result", text);
                writer.WriteEndObject();
            }
            using var wrapped = JsonDocument.Parse(buffer.WrittenMemory);
            return wrapped.RootElement.Clone();
        }
    }
}
