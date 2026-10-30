using DigitalBrain.Capabilities;
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

    private static readonly Lock Sync = new();
    private static readonly List<FriendCapabilityShare> Shares = [];
    private static readonly List<(string ConnectionKey, string FriendApp)> BoundFriendConnections = [];

    public static FriendCapabilityShare RegisterFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var scope = string.IsNullOrWhiteSpace(capabilityOrScope) ? WildcardScope : capabilityOrScope.Trim();
        var share = new FriendCapabilityShare(normalizedOwner, normalizedFriend, scope, DateTimeOffset.UtcNow);
        lock (Sync)
        {
            Shares.RemoveAll(s => string.Equals(s.OwnerAppId, normalizedOwner, StringComparison.Ordinal)
                && string.Equals(s.FriendAppId, normalizedFriend, StringComparison.Ordinal)
                && string.Equals(s.Scope, scope, StringComparison.Ordinal));
            Shares.Add(share);
        }
        return share;
    }

    public static bool RevokeFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId) || string.IsNullOrWhiteSpace(friendAppId)) { return false; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(capabilityOrScope))
            {
                return Shares.RemoveAll(s => string.Equals(s.OwnerAppId, normalizedOwner, StringComparison.Ordinal)
                    && string.Equals(s.FriendAppId, normalizedFriend, StringComparison.Ordinal)) > 0;
            }
            var scope = capabilityOrScope.Trim();
            return Shares.RemoveAll(s => string.Equals(s.OwnerAppId, normalizedOwner, StringComparison.Ordinal)
                && string.Equals(s.FriendAppId, normalizedFriend, StringComparison.Ordinal)
                && string.Equals(s.Scope, scope, StringComparison.Ordinal)) > 0;
        }
    }

    public static bool IsFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId) || string.IsNullOrWhiteSpace(friendAppId)) { return false; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        if (string.Equals(normalizedOwner, normalizedFriend, StringComparison.Ordinal)) { return true; }
        var scope = capabilityOrScope?.Trim();
        lock (Sync)
        {
            return Shares.Any(s => string.Equals(s.OwnerAppId, normalizedOwner, StringComparison.Ordinal)
                && string.Equals(s.FriendAppId, normalizedFriend, StringComparison.Ordinal)
                && (string.Equals(s.Scope, WildcardScope, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(scope) && string.Equals(s.Scope, scope, StringComparison.Ordinal))));
        }
    }

    public static IReadOnlyList<FriendCapabilityShare> FriendsOf(string ownerAppId)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId)) { return []; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        lock (Sync)
        {
            return [.. Shares.Where(s => string.Equals(s.OwnerAppId, normalizedOwner, StringComparison.Ordinal))];
        }
    }

    public static IReadOnlyList<string> OwnersSharingWith(string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(friendAppId)) { return []; }
        var normalizedFriend = NormalizeAppId(friendAppId);
        lock (Sync)
        {
            return
            [
                .. Shares
                    .Where(s => string.Equals(s.FriendAppId, normalizedFriend, StringComparison.Ordinal)
                        && (s.Scope == WildcardScope || string.IsNullOrWhiteSpace(capabilityOrScope) || string.Equals(s.Scope, capabilityOrScope.Trim(), StringComparison.Ordinal)))
                    .Select(s => s.OwnerAppId)
                    .Distinct(StringComparer.Ordinal)
            ];
        }
    }

    public static void BindFriendConnection(string connectionKey, string friendAppId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var principalId = AppPrincipal.Id(normalizedFriend);
        lock (Sync)
        {
            if (!BoundFriendConnections.Contains((normalizedKey, normalizedFriend)))
            {
                BoundFriendConnections.Add((normalizedKey, normalizedFriend));
            }
            if (!BoundFriendConnections.Contains((normalizedKey, principalId)))
            {
                BoundFriendConnections.Add((normalizedKey, principalId));
            }
        }
    }

    public static void UnbindFriendConnection(string connectionKey, string friendAppId)
    {
        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(friendAppId)) { return; }
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var principalId = AppPrincipal.Id(normalizedFriend);
        lock (Sync)
        {
            BoundFriendConnections.RemoveAll(b => string.Equals(b.ConnectionKey, normalizedKey, StringComparison.Ordinal)
                && (string.Equals(b.FriendApp, normalizedFriend, StringComparison.Ordinal)
                    || string.Equals(b.FriendApp, principalId, StringComparison.Ordinal)));
        }
    }

    public static bool IsConnectionBoundToFriend(CapabilityCaller caller, string? targetOrConnectionKey)
    {
        if (caller is null || string.IsNullOrWhiteSpace(targetOrConnectionKey)) { return false; }
        var callerApp = caller.AppId is { Length: > 0 } app ? NormalizeAppId(app)
            : AppPrincipal.AppKeyOf(caller.PrincipalId);
        if (string.IsNullOrWhiteSpace(callerApp)) { return false; }

        var candidate = StripNeuronPrefix(targetOrConnectionKey);
        if (CheckBoundSet(candidate, callerApp)) { return true; }

        var slash = candidate.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0 && CheckBoundSet(candidate[(slash + 1)..], callerApp))
        {
            return true;
        }

        lock (Sync)
        {
            return Shares.Any(s =>
                string.Equals(s.FriendAppId, callerApp, StringComparison.Ordinal)
                && (string.Equals(StripNeuronPrefix(s.Scope), candidate, StringComparison.Ordinal)
                    || (slash >= 0 && string.Equals(StripNeuronPrefix(s.Scope), candidate[(slash + 1)..], StringComparison.Ordinal))));
        }
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
        lock (Sync)
        {
            Shares.Clear();
            BoundFriendConnections.Clear();
        }
    }

    private static bool CheckBoundSet(string key, string callerApp)
    {
        var principalId = AppPrincipal.Id(callerApp);
        lock (Sync)
        {
            return BoundFriendConnections.Any(b => string.Equals(b.ConnectionKey, key, StringComparison.Ordinal)
                && (string.Equals(b.FriendApp, callerApp, StringComparison.Ordinal)
                    || string.Equals(b.FriendApp, principalId, StringComparison.Ordinal)));
        }
    }

    private static string NormalizeAppId(string appId)
        => AppPrincipal.AppKeyOf(appId.Trim()) ?? appId.Trim();

    private static string StripNeuronPrefix(string value)
        => value.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal)
            ? value[ConnectionKeys.NeuronScheme.Length..]
            : value;
}
