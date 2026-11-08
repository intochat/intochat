using System.Text.Json;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Tests;

// The arguments of an Emit as the app's operations read them: a string is one, an array several.
internal static class EmitArgs
{
    public static string[] Of(Emit emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        return emit.Args is not { } args
            ? []
            : args.ValueKind switch
            {
                JsonValueKind.String => [args.GetString() ?? ""],
                JsonValueKind.Array => [.. args.EnumerateArray().Select(item => item.GetString() ?? "")],
                _ => [],
            };
    }
}
