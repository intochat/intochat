using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps;

// The signals and intents a compiled app's view offers: Emit(owner, signal, args) for the app's operations,
// Emits<TSignal>(appName, signal) for strongly typed screen reactions, Intent for what the client does itself.
public static class AppSignals
{
    public static Emit Emit(string owner, string signal, params string[] args)
    {
        var normalizedOwner = owner.StartsWith("app.", StringComparison.Ordinal) || owner.Contains('/') ? owner : "app." + owner;
        return new(normalizedOwner, signal, args.Length switch
        {
            0 => null,
            1 => DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(args[0], typeof(string)),
            _ => DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(args, typeof(string[])),
        });
    }

    public static string AliasOf(Type signalType)
        => signalType.GetCustomAttribute<SignalAttribute>(false)?.Alias ?? signalType.Name;

    public static Emit Emits<TSignal>(string appName, TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var owner = appName.StartsWith("app.", StringComparison.Ordinal) || appName.Contains('/') ? appName : "app." + appName;
        var encoded = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(signal, signal.GetType());
        return new Emit(owner, AliasOf(typeof(TSignal)), StripNullProperties(encoded));
    }

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
