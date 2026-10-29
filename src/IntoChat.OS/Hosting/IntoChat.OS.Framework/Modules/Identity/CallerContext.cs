using DigitalBrain.Capabilities;

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

// Kind and the trusted edge are one fact on a CapabilityCaller: the app id carries them. A null app is a person,
// and these three ids are edges rather than apps. Anything else is an app stamped by the app proxy.
public static class CapabilityCallers
{
    public const string PlatformApp = "platform";
    public const string SchedulerApp = "scheduler";
    public const string AssistantApp = "assistant";

    public static CapabilityCaller Of(string principalId, string accountId, string brainId, CallerKind kind = CallerKind.User, string? appId = null, string? conversationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var app = kind switch
        {
            CallerKind.Platform => PlatformApp,
            CallerKind.Scheduler => SchedulerApp,
            CallerKind.Assistant => AssistantApp,
            CallerKind.App => string.IsNullOrEmpty(appId) ? "app" : appId,
            _ => null,
        };
        return new CapabilityCaller(principalId, accountId, brainId, app, conversationId);
    }

    public static CapabilityCaller Platform(string principalId, string accountId, string brainId, string? conversationId = null)
        => Of(principalId, accountId, brainId, CallerKind.Platform, conversationId: conversationId);
}

public static class CapabilityCallerEdges
{
    extension(CapabilityCaller caller)
    {
        public CallerKind Kind => caller.AppId switch
        {
            null or "" => CallerKind.User,
            CapabilityCallers.PlatformApp => CallerKind.Platform,
            CapabilityCallers.SchedulerApp => CallerKind.Scheduler,
            CapabilityCallers.AssistantApp => CallerKind.Assistant,
            _ => CallerKind.App,
        };

        public TrustedEdge StampedBy => caller.Kind switch
        {
            CallerKind.App => TrustedEdge.AppProxy,
            CallerKind.Scheduler => TrustedEdge.Scheduler,
            CallerKind.Platform => TrustedEdge.Platform,
            _ => TrustedEdge.AuthenticatedHttp,
        };

        // The person behind the call. An app keeps the person's principal and names itself with AppId.
        public string PersonId => caller.PrincipalId;

        public string? OnBehalfOf => null;
    }
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
