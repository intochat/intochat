using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// OS module 'sdk': common capability and friend-style capability sharing across trusted apps.
[DigitalBrain.Module(Id)]
public sealed class SdkModule : DigitalBrain.Module
{
    public const string Id = "sdk";

    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}

public sealed record FriendCapabilityShare(
    string OwnerAppId,
    string FriendAppId,
    string Scope,
    DateTimeOffset SharedAt);

public static class SdkCapabilitySharing
{
    public const string WildcardScope = "*";

    public static FriendCapabilityShare RegisterFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var scope = string.IsNullOrWhiteSpace(capabilityOrScope) ? WildcardScope : capabilityOrScope.Trim();
        var share = new FriendCapabilityShare(normalizedOwner, normalizedFriend, scope, DateTimeOffset.UtcNow);

        var brain = ExtractBrain(normalizedOwner);
        var grant = new Grant
        {
            Id = $"friend:{normalizedOwner}:{normalizedFriend}:{scope}",
            Owner = $"app:{normalizedOwner}",
            Grantee = $"app:{normalizedFriend}",
            Target = scope,
            Scope = scope,
            WorkspaceId = brain,
            AppId = normalizedFriend,
            SemanticTypeId = scope,
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        EdgeGrantsCache.Current.AddGrant(brain, grant);
        return share;
    }

    public static bool RevokeFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId) || string.IsNullOrWhiteSpace(friendAppId)) { return false; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedOwner);
        EdgeGrantsCache.Current.RemoveGrant(brain, g =>
            (string.Equals(g.Owner, $"app:{normalizedOwner}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedOwner, StringComparison.Ordinal))
            && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(capabilityOrScope) || string.Equals(g.Scope, capabilityOrScope.Trim(), StringComparison.Ordinal)));
        return true;
    }

    public static bool IsFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId) || string.IsNullOrWhiteSpace(friendAppId)) { return false; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        if (string.Equals(normalizedOwner, normalizedFriend, StringComparison.Ordinal)) { return true; }
        var scope = capabilityOrScope?.Trim();
        var brain = ExtractBrain(normalizedOwner);
        var grants = EdgeGrantsCache.Current.GetGrantsAsync(brain).GetAwaiter().GetResult();
        return grants.Any(g => !g.Revoked
            && (string.Equals(g.Owner, $"app:{normalizedOwner}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedOwner, StringComparison.Ordinal))
            && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))
            && (string.Equals(g.Scope, WildcardScope, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(scope) && string.Equals(g.Scope, scope, StringComparison.Ordinal))));
    }

    public static IReadOnlyList<FriendCapabilityShare> FriendsOf(string ownerAppId)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId)) { return []; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var brain = ExtractBrain(normalizedOwner);
        var grants = EdgeGrantsCache.Current.GetGrantsAsync(brain).GetAwaiter().GetResult();
        return grants
            .Where(g => !g.Revoked && (string.Equals(g.Owner, $"app:{normalizedOwner}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedOwner, StringComparison.Ordinal)))
            .Select(g => new FriendCapabilityShare(normalizedOwner, NormalizeAppId(g.Grantee), g.Scope, g.GrantedAt))
            .ToArray();
    }

    public static IReadOnlyList<string> OwnersSharingWith(string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(friendAppId)) { return []; }
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedFriend);
        var grants = EdgeGrantsCache.Current.GetGrantsAsync(brain).GetAwaiter().GetResult();
        return grants
            .Where(g => !g.Revoked
                && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))
                && (string.Equals(g.Scope, WildcardScope, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(capabilityOrScope) || string.Equals(g.Scope, capabilityOrScope.Trim(), StringComparison.Ordinal)))
            .Select(g => NormalizeAppId(g.Owner))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static void BindFriendConnection(string connectionKey, string friendAppId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedKey);
        var grant = new Grant
        {
            Id = $"binding:{normalizedKey}:{normalizedFriend}",
            Owner = $"brain:{brain}",
            Grantee = $"app:{normalizedFriend}",
            Target = normalizedKey,
            Scope = "*",
            WorkspaceId = brain,
            AppId = normalizedFriend,
            SemanticTypeId = "*",
            TargetNeuronKey = normalizedKey,
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        EdgeGrantsCache.Current.AddGrant(brain, grant);
    }

    public static void UnbindFriendConnection(string connectionKey, string friendAppId)
    {
        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(friendAppId)) { return; }
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedKey);
        EdgeGrantsCache.Current.RemoveGrant(brain, g =>
            (string.Equals(g.Target, normalizedKey, StringComparison.Ordinal) || string.Equals(g.TargetNeuronKey, normalizedKey, StringComparison.Ordinal))
            && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal)));
    }

    public static bool IsConnectionBoundToFriend(CapabilityCaller caller, string? targetOrConnectionKey)
    {
        if (caller is null || string.IsNullOrWhiteSpace(targetOrConnectionKey)) { return false; }
        var targetBrain = ExtractBrain(targetOrConnectionKey);
        if (EdgeGrantsCache.Current.IsConnectionBoundAsync(targetBrain, caller, targetOrConnectionKey).GetAwaiter().GetResult())
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(caller.BrainId) && !string.Equals(caller.BrainId, targetBrain, StringComparison.OrdinalIgnoreCase))
        {
            return EdgeGrantsCache.Current.IsConnectionBoundAsync(caller.BrainId, caller, targetOrConnectionKey).GetAwaiter().GetResult();
        }
        return false;
    }

    public static async Task ShareConnectionWithFriendAsync(IConnection connection, string ownerAppId, string friendAppId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RegisterFriend(ownerAppId, friendAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        await connection.ShareWith(AppPrincipal.Id(normalizedFriend)).ConfigureAwait(false);
    }

    public static Task<bool> IsConnectionSharedWithFriendAsync(IConnection connection, string friendAppId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var normalizedFriend = NormalizeAppId(friendAppId);
        return connection.IsSharedWith(AppPrincipal.Id(normalizedFriend));
    }

    public static Grant CreateFriendGrant(
        string workspaceId,
        string ownerAppId,
        string friendAppId,
        string semanticTypeId,
        string? targetNeuronKey = null,
        GrantMode mode = GrantMode.Always)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticTypeId);
        RegisterFriend(ownerAppId, friendAppId, semanticTypeId);
        if (!string.IsNullOrWhiteSpace(targetNeuronKey))
        {
            BindFriendConnection(targetNeuronKey, friendAppId);
        }
        return new Grant
        {
            WorkspaceId = workspaceId,
            AppId = NormalizeAppId(friendAppId),
            SemanticTypeId = semanticTypeId,
            TargetNeuronKey = targetNeuronKey,
            Mode = mode,
            GrantedAt = DateTimeOffset.UtcNow,
            Revoked = false,
        };
    }

    public static void ClearForTests()
    {
        EdgeGrantsCache.Current.Clear();
    }

    private static string ExtractBrain(string keyOrApp)
    {
        var clean = StripNeuronPrefix(keyOrApp);
        if (clean.StartsWith("app:", StringComparison.Ordinal)) { clean = clean["app:".Length..]; }
        if (clean.StartsWith("brain:", StringComparison.Ordinal)) { clean = clean["brain:".Length..]; }
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? clean[..slash] : "local";
    }

    private static string NormalizeAppId(string appId)
        => AppPrincipal.AppKeyOf(appId.Trim()) ?? appId.Trim();

    private static string StripNeuronPrefix(string value)
        => value.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal)
            ? value[ConnectionKeys.NeuronScheme.Length..]
            : value;
}
