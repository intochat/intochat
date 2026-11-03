using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Log;
using System.Collections.Concurrent;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using IntoChat.OS.Authority.Identity.Contacts;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Identity.Grants;

public interface IEdgeGrantsCache
{
    Task<IReadOnlyList<Grant>> GetGrantsAsync(string brain, CancellationToken ct = default);
    Task<PrincipalSet> ExpandAsync(string ownerBrain, CapabilityCaller caller);
    Task<bool> IsGrantedAsync(string brain, CapabilityCaller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default);
    Task<bool> IsConnectionBoundAsync(string brain, CapabilityCaller caller, string targetOrConnectionKey, CancellationToken ct = default);
    void Invalidate(string brain);
    void Clear();
}

public sealed class EdgeGrantsCache : IEdgeGrantsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // The authority reads the grants and contacts it decides from as the platform: under the caller being decided,
    // those reads would be judged by the very grants they fetch. It reads inside the brain it reads from.
    private static CapabilityCaller AuthorityReader(string brain) => CapabilityCallers.Platform("identity.grants", "system", brain);
    public static EdgeGrantsCache Current { get; set; } = new();

    private readonly Lock _sync = new();
    private readonly ConcurrentDictionary<string, CachedGrantsEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly struct ExpansionKey(string ownerBrain, string callerKey) : IEquatable<ExpansionKey>
    {
        public string OwnerBrain { get; } = ownerBrain;
        public string CallerKey { get; } = callerKey;

        public bool Equals(ExpansionKey other) =>
            string.Equals(OwnerBrain, other.OwnerBrain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CallerKey, other.CallerKey, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ExpansionKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(OwnerBrain), StringComparer.Ordinal.GetHashCode(CallerKey));
    }

    private sealed record CachedExpansionEntry(PrincipalSet Principals, DateTimeOffset CachedAt);
    private readonly ConcurrentDictionary<ExpansionKey, CachedExpansionEntry> _expansions = new();

    private readonly IServiceProvider? _services;
    private readonly IClusterListeners? _listeners;
    private readonly TimeProvider _clock;

    private sealed record CachedGrantsEntry(Grant[] Grants, long Version, DateTimeOffset CachedAt);

    public EdgeGrantsCache(IServiceProvider? services = null, IClusterListeners? listeners = null, TimeProvider? clock = null)
    {
        _services = services;
        _listeners = listeners;
        _clock = clock ?? TimeProvider.System;
        Current = this;

        if (listeners is not null)
        {
            _ = listeners.Listen<GrantsChanged>("edge-grants", (publisher, signal, ct) =>
            {
                Invalidate(publisher.Brain.Value);
                return Task.CompletedTask;
            });
            _ = listeners.Listen<ContactsChanged>("edge-contacts", (publisher, signal, ct) =>
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
                using var reader = RuntimeCallerContext.Stamp(AuthorityReader(brain));
                var grantsNeuron = ResolveNeuron<IGrantsNeuron>(IGrantsNeuron.Key(brain));
                if (grantsNeuron is not null)
                {
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
            }
            catch (Exception error) when (cached is not null)
            {
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Fallback to cached grants on network or silo timeout");
                return cached.Grants;
            }
        }

        return cached?.Grants ?? [];
    }

    public async Task<PrincipalSet> ExpandAsync(string ownerBrain, CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var set = PrincipalSet.For(caller);
        if (string.IsNullOrWhiteSpace(ownerBrain))
        {
            return set;
        }

        var callerKey = $"{caller.BrainId}:{caller.AppId}:{caller.PrincipalId}";
        var key = new ExpansionKey(ownerBrain, callerKey);
        var now = _clock.GetUtcNow();

        if (_expansions.TryGetValue(key, out var cached) && (now - cached.CachedAt) < Ttl)
        {
            return cached.Principals;
        }

        if (_services is not null && !string.IsNullOrWhiteSpace(ownerBrain))
        {
            try
            {
                using var reader = RuntimeCallerContext.Stamp(AuthorityReader(ownerBrain));
                var contactsNeuron = ResolveNeuron<IContactsNeuron>(IContactsNeuron.Key(ownerBrain));
                if (contactsNeuron is not null)
                {
                    var usersets = new List<string>();
                    if (!string.IsNullOrWhiteSpace(caller.BrainId))
                    {
                        var lists = await contactsNeuron.ListsContaining($"brain:{caller.BrainId}").ConfigureAwait(false);
                        if (lists is not null)
                        {
                            foreach (var list in lists)
                            {
                                usersets.Add(IContactsNeuron.Userset(list));
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(caller.AppId))
                    {
                        var lists = await contactsNeuron.ListsContaining($"app:{caller.AppId}").ConfigureAwait(false);
                        if (lists is not null)
                        {
                            foreach (var list in lists)
                            {
                                usersets.Add(IContactsNeuron.Userset(list));
                            }
                        }
                    }
                    if (usersets.Count > 0)
                    {
                        set = set.With([.. usersets]);
                    }
                    _expansions[key] = new CachedExpansionEntry(set, now);
                    return set;
                }
            }
            catch (Exception error) when (cached is not null)
            {
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Fallback to cached expansion on network or silo timeout");
                return cached.Principals;
            }
        }

        return cached?.Principals ?? set;
    }

    public async Task<bool> IsGrantedAsync(string brain, CapabilityCaller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default)
    {
        var grants = await GetGrantsAsync(brain, ct).ConfigureAwait(false);
        var principals = await ExpandAsync(brain, caller).ConfigureAwait(false);
        return GrantRules.IsGranted(semanticTypeId, targetNeuron ?? "", grants, principals, caller);
    }

    private T? ResolveNeuron<T>(string key) where T : class
    {
        if (_services is null)
        {
            return null;
        }

        if (_services.GetService(typeof(Orleans.IGrainFactory)) is not null)
        {
            return SiloNeurons.Get<T>(_services, key);
        }

        if (_services.GetService(typeof(T)) is T resolved)
        {
            return resolved;
        }

        if (_listeners is not null)
        {
            var lType = _listeners.GetType();
            var osMember = lType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType.Name == "TestOS") as System.Reflection.MemberInfo
                ?? lType.GetProperties(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(p => p.PropertyType.Name == "TestOS");

            if (osMember is not null)
            {
                var os = osMember is System.Reflection.FieldInfo fi ? fi.GetValue(_listeners) : ((System.Reflection.PropertyInfo)osMember).GetValue(_listeners);
                if (os is not null)
                {
                    var id = SiloNeurons.IdOf(typeof(T), key);
                    var getMethod = os.GetType().GetMethod("Get", [typeof(DigitalBrain.NeuronId)])?.MakeGenericMethod(typeof(T));
                    if (getMethod?.Invoke(os, [id]) is T testNeuron)
                    {
                        return testNeuron;
                    }
                }
            }
        }

        return SiloNeurons.Get<T>(_services, key);
    }

    public async Task<bool> IsConnectionBoundAsync(string brain, CapabilityCaller caller, string targetOrConnectionKey, CancellationToken ct = default)
    {
        if (caller is null || string.IsNullOrWhiteSpace(targetOrConnectionKey)) { return false; }
        var callerAppKey = GrantRules.AppOf(caller);
        if (string.IsNullOrEmpty(callerAppKey)) { return false; }

        var clean = StripNeuronPrefix(targetOrConnectionKey);
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        var canonicalKey = slash >= 0 && ConnectionKeys.Is(clean[(slash + 1)..]) ? clean[(slash + 1)..] : clean;
        var ownerBrain = ExtractBrain(canonicalKey);
        if (string.Equals(ownerBrain, "local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(brain) && !string.Equals(brain, "local", StringComparison.OrdinalIgnoreCase))
        {
            ownerBrain = brain;
        }

        var targets = new List<string> { canonicalKey };
        if (!string.Equals(canonicalKey, targetOrConnectionKey, StringComparison.Ordinal))
        {
            targets.Add(targetOrConnectionKey);
        }
        if (!canonicalKey.StartsWith(ownerBrain + "/", StringComparison.Ordinal))
        {
            targets.Add($"{ownerBrain}/{canonicalKey}");
        }

        foreach (var target in targets)
        {
            if (await IsGrantedAsync(ownerBrain, caller, "use-via:" + callerAppKey, target, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    // The target's brain answers first, then the caller's own. Grain turns await this one.
    public async Task<bool> IsConnectionBoundAsync(CapabilityCaller caller, string? targetOrConnectionKey)
    {
        if (caller is null || string.IsNullOrWhiteSpace(targetOrConnectionKey)) { return false; }
        var targetBrain = ExtractBrain(targetOrConnectionKey);
        if (await IsConnectionBoundAsync(targetBrain, caller, targetOrConnectionKey)) { return true; }
        return !string.IsNullOrWhiteSpace(caller.BrainId) && !string.Equals(caller.BrainId, targetBrain, StringComparison.OrdinalIgnoreCase)
            && await IsConnectionBoundAsync(caller.BrainId, caller, targetOrConnectionKey);
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

    public void BindFriendConnection(string connectionKey, string friendAppId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedKey);
        var grant = Grant.Tuple($"brain:{brain}", $"app:{normalizedFriend}", normalizedKey, $"use-via:{normalizedFriend}") with
        {
            Id = $"binding:{normalizedKey}:{normalizedFriend}",
        };
        AddGrant(brain, grant);
    }

    public void UnbindFriendConnection(string connectionKey, string friendAppId)
    {
        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(friendAppId)) { return; }
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        var brain = ExtractBrain(normalizedKey);
        RemoveGrant(brain, g => string.Equals(g.Id, $"binding:{normalizedKey}:{normalizedFriend}", StringComparison.Ordinal)
            || ((string.Equals(g.Target, normalizedKey, StringComparison.Ordinal) || string.Equals(g.TargetNeuronKey, normalizedKey, StringComparison.Ordinal))
                && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))));
    }

    private static string NormalizeAppId(string appId)
        => AppPrincipal.AppKeyOf(appId.Trim()) ?? appId.Trim();

    internal static string ExtractBrain(string? keyOrApp)
    {
        if (string.IsNullOrWhiteSpace(keyOrApp)) { return "local"; }
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
                foreach (var key in _expansions.Keys)
                {
                    if (string.Equals(key.OwnerBrain, brain, StringComparison.OrdinalIgnoreCase))
                    {
                        _expansions.TryRemove(key, out _);
                    }
                }
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
            _expansions.Clear();
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
