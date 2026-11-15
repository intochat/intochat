using DigitalBrain.Capabilities;

namespace IntoChat.OS.Authority.Identity;

// Everything an anonymous /try visitor may reach: the shipped demo screens, through three MCP tools, on endpoints marked
// with Door. Whatever is not named here is refused.
public static class GuestSurface
{
    // Reserved for guests: no principal can be registered under it (PrincipalIdIsAvailable), so the principal alone marks a
    // guest, on every hop and after MCP re-stamps the caller as the agent.
    public const string BrainPrefix = "try-";

    // A guest is never a person: its session is stamped as this app, as unproved loopback traffic is stamped LocalAppId.
    public const string AppId = "guest";

    public static IReadOnlyList<string> Tools { get; } = ["screen_list", "screen_read", "screen_act"];

    public static IReadOnlyList<string> Screens { get; } = ["helloworld", "btcalert", "photocraft"];

    public static bool Is(CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.PrincipalId.StartsWith(BrainPrefix, StringComparison.Ordinal);
    }

    public static bool MayUseTool(string? tool) => tool is not null && Tools.Contains(tool, StringComparer.Ordinal);

    // The app id exactly as the caller sent it: no case folding, trimming or path form.
    public static bool MayOpen(string? appId) => appId is not null && Screens.Contains(appId, StringComparer.Ordinal);

    // Marks an endpoint a guest may be admitted on. A guest is anonymous on every endpoint without it.
    internal sealed class Door
    {
        public static readonly Door Instance = new();
    }
}
