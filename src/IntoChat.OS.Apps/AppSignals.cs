using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps;

// The signals and intents a compiled app's view offers: Emit(owner, signal, args) for the app's operations,
// Emits<TSignal>(appName, signal) for strongly typed screen reactions, Intent for what the client does itself.
public static class AppSignals
{
    private static readonly JsonSerializerOptions EmitJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Emit Emit(string owner, string signal, params string[] args)
    {
        var normalizedOwner = owner.StartsWith("app.", StringComparison.Ordinal) || owner.Contains('/') ? owner : "app." + owner;
        return new(normalizedOwner, signal, args.Length switch
        {
            0 => null,
            1 => JsonSerializer.SerializeToElement(args[0]),
            _ => JsonSerializer.SerializeToElement(args),
        });
    }

    public static string AliasOf(Type signalType)
        => signalType.GetCustomAttribute<SignalAttribute>(false)?.Alias ?? signalType.Name;

    public static Emit Emits<TSignal>(string appName, TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var owner = appName.StartsWith("app.", StringComparison.Ordinal) || appName.Contains('/') ? appName : "app." + appName;
        return new Emit(owner, AliasOf(typeof(TSignal)), JsonSerializer.SerializeToElement(signal, signal.GetType(), EmitJson));
    }

    public static Intent Copy(string text) => new(IntentKind.Copy, text);

    public static Intent Open(string target) => new(IntentKind.OpenWindow, target);

    // Opens the app's window; the first argument is handed to it as its open input (an app's view).
    public static Intent OpenApp(string app, string? input = null)
        => Open($"os://window/open?app={app}" + (string.IsNullOrEmpty(input) ? "" : $"&input={Uri.EscapeDataString(input)}"));
}
