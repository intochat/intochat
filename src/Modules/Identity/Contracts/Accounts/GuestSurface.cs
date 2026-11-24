using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

// Everything an anonymous /try visitor may reach: the shipped demo screens, through three MCP tools, on endpoints marked
// with Door. Whatever is not named here is refused.
public static class GuestSurface
{
    // Reserved for guests: sign-up refuses it (PrincipalIdIsAvailable, PrincipalPasswords.Register), so the principal alone
    // marks a guest, also after MCP re-stamps the caller as the agent. The directory's Ensure* calls do not check it: that is
    // how the guest itself is made.
    public const string BrainPrefix = "try-";

    // A guest is never a person: its session is stamped as this app, and never inherits Actor.Person.
    public const string AppId = "guest";

    public static IReadOnlyList<string> Tools { get; } = ["screen_list", "screen_read", "screen_act"];

    public static IReadOnlyList<string> Screens { get; } = ["helloworld", "btcalert", "photocraft"];

    public static bool Is(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.Actor is Actor.App { Key: AppId } || IsGuestPrincipal(caller.For?.Principal);
    }

    public static bool IsGuestPrincipal(string? principal) => principal?.StartsWith(BrainPrefix, StringComparison.Ordinal) == true;

    // The guest a trusted issuer's try session makes on this install (a guest ticket, GET /identity/external/guest): its principal,
    // account and brain id. The issuer scopes it, so two issuers' sessions never meet in one guest; the issuer computes the same id
    // to send the visitor to its brain.
    public static string PrincipalOf(string issuer, string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        return BrainPrefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(issuer + "\n" + session)))[..32];
    }

    public static bool MayUseTool(string? tool) => tool is not null && Tools.Contains(tool, StringComparer.Ordinal);

    // The app id exactly as the caller sent it: no case folding, trimming or path form.
    public static bool MayOpen(string? appId) => appId is not null && Screens.Contains(appId, StringComparer.Ordinal);

    // Marks an endpoint a guest may be admitted on. A guest is anonymous on every endpoint without it.
    public sealed class Door
    {
        public static readonly Door Instance = new();
    }
}
