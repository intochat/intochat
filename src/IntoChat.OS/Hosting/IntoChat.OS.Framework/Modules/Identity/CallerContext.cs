namespace IntoChat.OS.Framework.Modules;

public enum CallerKind
{
    User = 0,
    Assistant = 1,
    App = 2,
    Scheduler = 3,
    Platform = 4,
}

public enum TrustedEdge
{
    AuthenticatedHttp = 0,
    AppProxy = 1,
    Scheduler = 2,
    Platform = 3,
}

// Stamped only at a trusted edge and re-stamped (never inherited) when a call enters or leaves an app.
[GenerateSerializer, Alias("enforcement.caller-context")]
public sealed record CallerContext
{
    [Id(0)] public required string PrincipalId { get; init; }
    [Id(1)] public required string AccountId { get; init; }
    [Id(2)] public required string BrainId { get; init; }
    [Id(3)] public required CallerKind Kind { get; init; }
    [Id(4)] public required TrustedEdge StampedBy { get; init; }
    [Id(5)] public string? AppId { get; init; }
    [Id(6)] public string? ConversationId { get; init; }
    // The person an app principal acts for. Stamped by the app proxy next to the app's own principal; null otherwise.
    [Id(7)] public string? OnBehalfOf { get; init; }

    // The person behind the call: the principal itself, or the one an app acts for.
    public string PersonId => OnBehalfOf ?? PrincipalId;
}

// An installed app's principal: deterministic from its app key ({brain}/apps/{name}), so the app proxy, grants and
// connection sharing name the same principal without a lookup. The brain's owner is recorded on its App membership.
public static class AppPrincipal
{
    public const string Prefix = "app:";

    public static string Id(string appKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        return Prefix + appKey;
    }

    public static bool IsApp(string? principalId) => principalId is { Length: > 4 } && principalId.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? AppKeyOf(string? principalId) => IsApp(principalId) ? principalId![Prefix.Length..] : null;
}
