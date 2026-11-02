using System.Collections.Concurrent;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Diagnostics;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Identity.Grants;

public interface IEdgeGrantsCache
{
    Task<IReadOnlyList<Grant>> GetGrantsAsync(string brain, CancellationToken ct = default);
    Task<bool> IsGrantedAsync(string brain, CapabilityCaller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default);
    Task<bool> IsConnectionBoundAsync(string brain, CapabilityCaller caller, string targetOrConnectionKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> OwnersSharingWithAsync(string friendAppId, string? capabilityOrScope = null, CancellationToken ct = default);
    void Invalidate(string brain);
    void Clear();
}

public sealed class EdgeGrantsCache : IEdgeGrantsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    public static EdgeGrantsCache Current { get; set; } = new();

    private readonly Lock _sync = new();
    private readonly ConcurrentDictionary<string, CachedGrantsEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider? _services;
    private readonly TimeProvider _clock;

    private sealed record CachedGrantsEntry(Grant[] Grants, long Version, DateTimeOffset CachedAt)
    {
        public bool HasFriendGrants { get; } = Grants.Any(g =>
            !string.IsNullOrWhiteSpace(g.Owner) &&
            (!string.IsNullOrWhiteSpace(g.Grantee) || !string.IsNullOrWhiteSpace(g.AppId)));
    }

    public EdgeGrantsCache(IServiceProvider? services = null, IClusterListeners? listeners = null, TimeProvider? clock = null)
    {
        _services = services;
        _clock = clock ?? TimeProvider.System;
        Current = this;

        if (listeners is not null)
        {
            _ = listeners.Listen<GrantsChanged>("edge-grants", (publisher, signal, ct) =>
            {
                Invalidate(publisher.Brain.Value);
                return Task.CompletedTask;
            });
        }
    }

    public async Task<IReadOnlyList<Grant>> GetGrantsAsync(string brain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brain)) { return []; }
        var now = _clock.GetUtcNow();

        if (_cache.TryGetValue(brain, out var cached) && (now - cached.CachedAt) < Ttl)
        {
            return cached.Grants;
        }

        if (_services is not null)
        {
            try
            {
                var grantsNeuron = SiloNeurons.Get<IGrantsNeuron>(_services, IGrantsNeuron.Key(brain));
                var grants = await grantsNeuron.List().WaitAsync(ct).ConfigureAwait(false);
                var version = await grantsNeuron.GetVersion().WaitAsync(ct).ConfigureAwait(false);
                var entry = new CachedGrantsEntry(grants, version, now);
                lock (_sync)
                {
                    if (!_cache.TryGetValue(brain, out var current) || current.Version <= entry.Version)
                    {
                        _cache[brain] = entry;
                    }
                }
                return grants;
            }
            catch (Exception error) when (cached is not null)
            {
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Fallback to cached grants on network or silo timeout");
                return cached.Grants;
            }
        }

        return cached?.Grants ?? [];
    }

    public async Task<bool> IsGrantedAsync(string brain, CapabilityCaller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default)
    {
        var grants = await GetGrantsAsync(brain, ct).ConfigureAwait(false);
        return GrantRules.IsGranted(caller, semanticTypeId, grants, targetNeuron);
    }

    public async Task<bool> IsConnectionBoundAsync(string brain, CapabilityCaller caller, string targetOrConnectionKey, CancellationToken ct = default)
    {
        if (caller is null || string.IsNullOrWhiteSpace(targetOrConnectionKey)) { return false; }
        var callerApp = GrantRules.AppOf(caller);
        if (string.IsNullOrWhiteSpace(callerApp)) { return false; }

        var targetBrain = ExtractBrain(targetOrConnectionKey);
        var grants = await GetGrantsAsync(targetBrain, ct).ConfigureAwait(false);
        if (grants.Count == 0 && !string.IsNullOrWhiteSpace(brain) && !string.Equals(brain, targetBrain, StringComparison.OrdinalIgnoreCase))
        {
            grants = await GetGrantsAsync(brain, ct).ConfigureAwait(false);
        }
        if (grants.Count == 0 && !string.IsNullOrWhiteSpace(caller.BrainId) && !string.Equals(caller.BrainId, targetBrain, StringComparison.OrdinalIgnoreCase) && !string.Equals(caller.BrainId, brain, StringComparison.OrdinalIgnoreCase))
        {
            grants = await GetGrantsAsync(caller.BrainId, ct).ConfigureAwait(false);
        }

        var candidate = StripNeuronPrefix(targetOrConnectionKey);
        var firstSlash = candidate.IndexOf('/', StringComparison.Ordinal);
        var canonicalKey = firstSlash >= 0 && ConnectionKeys.Is(candidate[(firstSlash + 1)..])
            ? candidate[(firstSlash + 1)..]
            : candidate;

        var relSlash = canonicalKey.IndexOf('/', StringComparison.Ordinal);
        var unqualifiedCandidate = relSlash >= 0 ? canonicalKey[(relSlash + 1)..] : canonicalKey;

        return grants.Any(g =>
        {
            if (g.Revoked) { return false; }
            var isCaller = string.Equals(g.AppId, callerApp, StringComparison.Ordinal)
                || string.Equals(g.Grantee, $"app:{callerApp}", StringComparison.Ordinal)
                || string.Equals(g.Grantee, caller.PrincipalId, StringComparison.Ordinal)
                || string.Equals(g.Grantee, $"brain:{caller.BrainId}", StringComparison.Ordinal);
            if (!isCaller) { return false; }

            var grantBrain = !string.IsNullOrWhiteSpace(g.WorkspaceId)
                ? g.WorkspaceId
                : ExtractBrain(g.Target ?? g.TargetNeuronKey ?? "");
            var isSameBrain = string.Equals(targetBrain, "local", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(targetBrain) && string.Equals(grantBrain, targetBrain, StringComparison.OrdinalIgnoreCase));

            // Exact match (always allowed across brains if grant explicitly named the target connection)
            if (MatchesTarget(g.Target, candidate, allowUnqualified: false)
                || MatchesTarget(g.Target, canonicalKey, allowUnqualified: false)
                || MatchesTarget(g.TargetNeuronKey, candidate, allowUnqualified: false)
                || MatchesTarget(g.TargetNeuronKey, canonicalKey, allowUnqualified: false))
            {
                return true;
            }

            // Cross-brain unqualified matching is strictly forbidden
            if (!isSameBrain)
            {
                return false;
            }

            // Same-brain unqualified / relative matching
            return MatchesTarget(g.Target, candidate, allowUnqualified: true)
                || MatchesTarget(g.Target, canonicalKey, allowUnqualified: true)
                || MatchesTarget(g.Target, unqualifiedCandidate, allowUnqualified: true)
                || MatchesTarget(g.TargetNeuronKey, candidate, allowUnqualified: true)
                || MatchesTarget(g.TargetNeuronKey, canonicalKey, allowUnqualified: true)
                || MatchesTarget(g.TargetNeuronKey, unqualifiedCandidate, allowUnqualified: true);
        });
    }

    public bool IsFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId) || string.IsNullOrWhiteSpace(friendAppId)) { return false; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        if (string.Equals(normalizedOwner, normalizedFriend, StringComparison.Ordinal)) { return true; }
        var scope = capabilityOrScope?.Trim();
        var brain = ExtractBrain(normalizedOwner);
        var grants = GetGrantsAsync(brain).GetAwaiter().GetResult();
        return grants.Any(g => !g.Revoked
            && (string.Equals(g.Owner, $"app:{normalizedOwner}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedOwner, StringComparison.Ordinal))
            && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))
            && (string.Equals(g.Scope, "*", StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(scope) && string.Equals(g.Scope, scope, StringComparison.Ordinal))));
    }

    public bool IsConnectionBound(CapabilityCaller caller, string? targetOrConnectionKey)
    {
        if (caller is null || string.IsNullOrWhiteSpace(targetOrConnectionKey)) { return false; }
        var targetBrain = ExtractBrain(targetOrConnectionKey);
        if (IsConnectionBoundAsync(targetBrain, caller, targetOrConnectionKey).GetAwaiter().GetResult())
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(caller.BrainId) && !string.Equals(caller.BrainId, targetBrain, StringComparison.OrdinalIgnoreCase))
        {
            return IsConnectionBoundAsync(caller.BrainId, caller, targetOrConnectionKey).GetAwaiter().GetResult();
        }
        return false;
    }

    public void RegisterFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var scope = string.IsNullOrWhiteSpace(capabilityOrScope) ? "*" : capabilityOrScope.Trim();

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
            GrantedAt = _clock.GetUtcNow(),
        };
        AddGrant(brain, grant);
    }

    public bool RevokeFriend(string ownerAppId, string friendAppId, string? capabilityOrScope = null)
    {
        if (string.IsNullOrWhiteSpace(ownerAppId) || string.IsNullOrWhiteSpace(friendAppId)) { return false; }
        var normalizedOwner = NormalizeAppId(ownerAppId);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedOwner);
        RemoveGrant(brain, g =>
            (string.Equals(g.Owner, $"app:{normalizedOwner}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedOwner, StringComparison.Ordinal))
            && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(capabilityOrScope) || string.Equals(g.Scope, capabilityOrScope.Trim(), StringComparison.Ordinal)));
        return true;
    }

    public void BindFriendConnection(string connectionKey, string friendAppId)
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
            GrantedAt = _clock.GetUtcNow(),
        };
        AddGrant(brain, grant);
    }

    public void UnbindFriendConnection(string connectionKey, string friendAppId)
    {
        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(friendAppId)) { return; }
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedKey);
        RemoveGrant(brain, g =>
            (string.Equals(g.Target, normalizedKey, StringComparison.Ordinal) || string.Equals(g.TargetNeuronKey, normalizedKey, StringComparison.Ordinal))
            && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal)));
    }

    public async Task<IReadOnlyList<string>> OwnersSharingWithAsync(string friendAppId, string? capabilityOrScope = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(friendAppId)) { return []; }
        var normalizedFriend = NormalizeAppId(friendAppId);
        if (string.IsNullOrWhiteSpace(normalizedFriend)) { return []; }

        // Ensure friend's brain grants are cached
        var friendBrain = ExtractBrain(normalizedFriend);
        if (!string.IsNullOrWhiteSpace(friendBrain) && !string.Equals(friendBrain, "local", StringComparison.OrdinalIgnoreCase))
        {
            await GetGrantsAsync(friendBrain, ct).ConfigureAwait(false);
        }
        else
        {
            await GetGrantsAsync("local", ct).ConfigureAwait(false);
        }

        // If capabilityOrScope contains a connection/target brain, ensure that brain's grants are cached
        if (!string.IsNullOrWhiteSpace(capabilityOrScope))
        {
            var targetBrain = ExtractBrain(capabilityOrScope);
            if (!string.IsNullOrWhiteSpace(targetBrain)
                && !string.Equals(targetBrain, "local", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetBrain, friendBrain, StringComparison.OrdinalIgnoreCase))
            {
                await GetGrantsAsync(targetBrain, ct).ConfigureAwait(false);
            }
        }

        var friendGrantee = $"app:{normalizedFriend}";
        var cleanScope = string.IsNullOrWhiteSpace(capabilityOrScope) ? null : StripNeuronPrefix(capabilityOrScope.Trim());
        HashSet<string>? owners = null;
        var now = _clock.GetUtcNow();

        foreach (var entry in _cache.Values)
        {
            if (!entry.HasFriendGrants) { continue; }
            if (_services is not null && (now - entry.CachedAt) >= Ttl) { continue; }

            var grants = entry.Grants;
            for (var i = 0; i < grants.Length; i++)
            {
                var g = grants[i];
                if (g.Revoked) { continue; }
                if (g.Expires is { } expires && expires <= now) { continue; }

                var granteeMatches = string.Equals(g.Grantee, friendGrantee, StringComparison.Ordinal)
                    || string.Equals(g.Grantee, normalizedFriend, StringComparison.Ordinal)
                    || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal);
                if (!granteeMatches) { continue; }

                var grantScope = StripNeuronPrefix(g.Scope ?? "");
                var scopeMatches = string.Equals(grantScope, "*", StringComparison.Ordinal)
                    || cleanScope is null
                    || string.Equals(grantScope, cleanScope, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.Target, cleanScope, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.TargetNeuronKey, cleanScope, StringComparison.OrdinalIgnoreCase);
                if (!scopeMatches) { continue; }

                if (string.IsNullOrWhiteSpace(g.Owner)) { continue; }
                var owner = NormalizeAppId(g.Owner);
                if (string.IsNullOrWhiteSpace(owner)
                    || owner.StartsWith("brain:", StringComparison.Ordinal)
                    || string.Equals(owner, normalizedFriend, StringComparison.Ordinal))
                {
                    continue;
                }

                owners ??= new HashSet<string>(StringComparer.Ordinal);
                owners.Add(owner);
            }
        }

        return owners is null || owners.Count == 0 ? [] : owners.ToArray();
    }

    private static string NormalizeAppId(string appId)
        => AppPrincipal.AppKeyOf(appId.Trim()) ?? appId.Trim();

    private static string ExtractBrain(string keyOrApp)
    {
        var clean = StripNeuronPrefix(keyOrApp);
        if (clean.StartsWith("app:", StringComparison.Ordinal)) { clean = clean["app:".Length..]; }
        var isBrainPrefixed = clean.StartsWith("brain:", StringComparison.Ordinal);
        if (isBrainPrefixed) { clean = clean["brain:".Length..]; }
        if (ConnectionKeys.TryParse(clean, out var conn)) { return conn.BrainId; }
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && ConnectionKeys.TryParse(clean[(slash + 1)..], out var nestedConn)) { return nestedConn.BrainId; }
        if (clean.StartsWith("connection/", StringComparison.OrdinalIgnoreCase)) { return "local"; }
        if (slash > 0) { return clean[..slash]; }
        if (isBrainPrefixed && !string.IsNullOrWhiteSpace(clean)) { return clean; }
        return "local";
    }

    public void Invalidate(string brain)
    {
        if (!string.IsNullOrWhiteSpace(brain))
        {
            lock (_sync)
            {
                _cache.TryRemove(brain, out _);
            }
        }
    }

    public void AddGrant(string brain, Grant grant)
    {
        var b = string.IsNullOrWhiteSpace(brain) ? "local" : brain;
        lock (_sync)
        {
            var grants = _cache.TryGetValue(b, out var entry) ? entry.Grants.ToList() : new List<Grant>();
            grants.RemoveAll(g => string.Equals(g.Id, grant.Id, StringComparison.Ordinal)
                || (string.Equals(g.Owner, grant.Owner, StringComparison.Ordinal)
                    && string.Equals(g.Grantee, grant.Grantee, StringComparison.Ordinal)
                    && string.Equals(g.Target, grant.Target, StringComparison.Ordinal)
                    && string.Equals(g.Scope, grant.Scope, StringComparison.Ordinal)));
            grants.Add(grant);
            _cache[b] = new CachedGrantsEntry(grants.ToArray(), (entry?.Version ?? 0) + 1, _clock.GetUtcNow());
        }
    }

    public void RemoveGrant(string brain, Func<Grant, bool> predicate)
    {
        var b = string.IsNullOrWhiteSpace(brain) ? "local" : brain;
        lock (_sync)
        {
            if (_cache.TryGetValue(b, out var entry))
            {
                var filtered = entry.Grants.Where(g => !predicate(g)).ToArray();
                _cache[b] = new CachedGrantsEntry(filtered, entry.Version + 1, _clock.GetUtcNow());
            }
        }
    }

    public void SetGrantsForTest(string brain, Grant[] grants, long version = 1)
    {
        lock (_sync)
        {
            _cache[brain] = new CachedGrantsEntry(grants, version, _clock.GetUtcNow());
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _cache.Clear();
        }
    }

    private static string StripNeuronPrefix(string value)
        => value.StartsWith("neuron://", StringComparison.Ordinal) ? value["neuron://".Length..] : value;

    private static bool MatchesTarget(string? grantTarget, string candidate, bool allowUnqualified = true)
    {
        if (string.IsNullOrWhiteSpace(grantTarget)) { return false; }
        var normalized = StripNeuronPrefix(grantTarget);
        if (string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
        if (!allowUnqualified) { return false; }
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && string.Equals(normalized[(slash + 1)..], candidate, StringComparison.OrdinalIgnoreCase);
    }
}
