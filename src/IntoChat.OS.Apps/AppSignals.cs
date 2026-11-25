using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps;

// The intents a compiled app's view offers: what the client does itself. A button's command is a typed signal (Kit's Sends).
public static class AppSignals
{
    private static JsonElement StripNullProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) { return element; }
        var hasNull = false;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) { hasNull = true; break; }
        }
        if (!hasNull) { return element; }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Null)
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    public static Intent Copy(string text) => new(IntentKind.Copy, text);

    public static Intent Open(string target) => new(IntentKind.OpenWindow, target);

    // Opens the app's window; the first argument is handed to it as its open input (an app's view).
    public static Intent OpenApp(string app, string? input = null)
        => Open($"os://window/open?app={app}" + (string.IsNullOrEmpty(input) ? "" : $"&input={Uri.EscapeDataString(input)}"));

    // Opens the app's window with strongly-typed arrival arguments and transition metadata for INavigatedTo<TArgs>.
    public static Intent OpenApp<TArgs>(
        string app,
        TArgs args,
        string transition = ViewTransition.FadeThrough,
        string? fromNeuron = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var json = args is null
            ? "{}"
            : StripNullProperties(DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(args, typeof(TArgs))).GetRawText();
        var normalizedTransition = string.IsNullOrWhiteSpace(transition)
            ? ViewTransition.FadeThrough
            : ViewTransition.Normalize(transition);
        var uri = $"os://window/open?app={app}&args={Uri.EscapeDataString(json)}&transition={Uri.EscapeDataString(normalizedTransition)}";
        if (!string.IsNullOrWhiteSpace(fromNeuron))
        {
            uri += $"&from={Uri.EscapeDataString(fromNeuron.Trim())}";
        }

        return Open(uri);
    }
}
