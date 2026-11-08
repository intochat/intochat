using System.Text.Json;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps;

// The signals and intents a compiled app's view offers: Emit(owner, signal, args) for the app's operations (a single argument
// is a JSON string, several a JSON array), Intent for what the client does itself.
public static class AppSignals
{
    public static Emit Emit(string owner, string signal, params string[] args) => new(owner, signal, args.Length switch
    {
        0 => null,
        1 => JsonSerializer.SerializeToElement(args[0]),
        _ => JsonSerializer.SerializeToElement(args),
    });

    public static Intent Copy(string text) => new(IntentKind.Copy, text);

    public static Intent Open(string target) => new(IntentKind.OpenWindow, target);

    // Opens the app's window; the first argument is handed to it as its open input (an app's view).
    public static Intent OpenApp(string app, string? input = null)
        => Open($"os://window/open?app={app}" + (string.IsNullOrEmpty(input) ? "" : $"&input={Uri.EscapeDataString(input)}"));
}
