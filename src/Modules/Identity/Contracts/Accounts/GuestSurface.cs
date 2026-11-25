using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

// Ephemeral/guest identity vocabulary and HTTP endpoint door marker.
// Capability gating for ephemeral brains is governed by DigitalBrain.Brains.IBrainCapabilityPolicy;
// Screens and MayOpen remain binary-compatible for intochat/cloud while retiring the legacy 3-screen toy restriction.
public static class GuestSurface
{
    // Reserved for ephemeral/try visitors: sign-up refuses it (PrincipalIdIsAvailable, PrincipalPasswords.Register),
    // so the principal alone marks an unclaimed ephemeral visitor, also after MCP re-stamps the caller as the agent.
    public const string BrainPrefix = "try-";

    // An unclaimed ephemeral visitor is never a durable person: its session is stamped as this app, and never inherits Actor.Person.
    public const string AppId = "guest";

    public static IReadOnlyList<string> Tools { get; } = ["screen_list", "screen_read", "screen_act"];

    // Retained for binary compatibility with intochat/cloud until Phase 2 cloud sync removes GuestTrySessionService.
    // Runtime screen gating is now governed by IBrainCapabilityPolicy rather than this legacy list.
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

    // Ephemeral brains may open any valid built-in or mini-app id in RAM (governed by IBrainCapabilityPolicy).
    // Rejects null, empty, whitespace, uppercase, dot-segments, and path separators to prevent traversal.
    public static bool MayOpen(string? appId)
    {
        if (string.IsNullOrEmpty(appId) || appId.Length > 64) { return false; }
        for (var i = 0; i < appId.Length; i++)
        {
            var c = appId[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (i > 0 && (c == '-' || c == '_'));
            if (!ok) { return false; }
        }
        return true;
    }

    // Marks an endpoint an ephemeral/guest session may reach. Operator, admin, and device endpoints stay closed without it.
    public sealed class Door
    {
        public static readonly Door Instance = new();
    }
}
