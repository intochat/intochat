using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity.Grants;

public interface IEdgeGrantsCache
{
    Task<IReadOnlyList<Grant>> GetGrantsAsync(string brain, CancellationToken ct = default);
    Task<PrincipalSet> ExpandAsync(string ownerBrain, Caller caller);
    Task<bool> IsGrantedAsync(string brain, Caller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default);
    Task<bool> IsConnectionBoundAsync(string brain, Caller caller, string targetOrConnectionKey, CancellationToken ct = default);
    Task<bool> IsConnectionBoundAsync(Caller caller, string? targetOrConnectionKey);
    void Invalidate(string brain);
    void Clear();
}

public sealed class EdgeGrantsCache : IEdgeGrantsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // The authority reads the grants and contacts it decides from as the kernel: under the caller being decided,
    // those reads would be judged by the very grants they fetch. It reads inside the brain it reads from.
    private Task<T> AsAuthority<T>(string brain, Func<Task<T>> read)
        => (_services?.GetService<EdgeAuthority>() ?? throw new InvalidOperationException("The grants authority reads only on a host with an edge authority."))
            .AsKernel(new BrainId(brain), read);
    public static EdgeGrantsCache Current { get; set; } = new();

    private readonly Lock _sync = new();
    private readonly ConcurrentDictionary<string, CachedGrantsEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceProvider? _services;
    private readonly TimeProvider _clock;
    private readonly Func<Type, NeuronId, object?>? _resolveNeuron;
    private readonly ILogger<EdgeGrantsCache>? _logger;

    private sealed record CachedGrantsEntry(Grant[] Grants, long Version, DateTimeOffset CachedAt);

    public EdgeGrantsCache(
        IServiceProvider? services = null,
        IClusterListeners? listeners = null,
        TimeProvider? clock = null,
        Func<Type, NeuronId, object?>? resolveNeuron = null)
    {
        _services = services;
        _clock = clock ?? TimeProvider.System;
        _resolveNeuron = resolveNeuron;
        _logger = services?.GetService<ILoggerFactory>()?.CreateLogger<EdgeGrantsCache>();
        if (services is null)
        {
            Current = this;
        }

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
                var grantsNeuron = ResolveNeuron<IGrantsNeuron>(IGrantsNeuron.Key(brain));
                if (grantsNeuron is not null)
                {
                    var (grants, version) = await AsAuthority(brain, async () =>
                        (await grantsNeuron.List().WaitAsync(ct).ConfigureAwait(false), await grantsNeuron.GetVersion().WaitAsync(ct).ConfigureAwait(false)))
                        .ConfigureAwait(false);
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
                LogFallback(error, "cached grants", brain);
                return cached.Grants;
            }
        }

        return cached?.Grants ?? [];
    }

    public Task<PrincipalSet> ExpandAsync(string ownerBrain, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return Task.FromResult(PrincipalSet.For(caller));
    }

    private void LogFallback(Exception error, string cacheKind, string brain)
    {
        if (_logger is not null)
        {
            _logger.LogWarning(error, "Falling back to {CacheKind} on network or silo timeout for brain {Brain}.", cacheKind, brain);
        }
        else
        {
            Trace.TraceWarning("Falling back to {0} for brain '{1}': {2}", cacheKind, brain, error.Message);
        }
    }

    public async Task<bool> IsGrantedAsync(string brain, Caller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default)
    {
        var grants = await GetGrantsAsync(brain, ct).ConfigureAwait(false);
        var principals = await ExpandAsync(brain, caller).ConfigureAwait(false);
        return GrantRules.IsGranted(semanticTypeId, targetNeuron ?? "", grants, principals, caller);
    }

    private T? ResolveNeuron<T>(string key) where T : class
    {
        if (_resolveNeuron is not null)
        {
            var id = SiloNeurons.IdOf(typeof(T), key);
            if (_resolveNeuron(typeof(T), id) is T testNeuron)
            {
                return testNeuron;
            }
        }

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

        return SiloNeurons.Get<T>(_services, key);
    }

    public async Task<bool> IsConnectionBoundAsync(string brain, Caller caller, string targetOrConnectionKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(targetOrConnectionKey) || !IsConnectionTarget(targetOrConnectionKey)) { return false; }
        var callerAppKey = GrantRules.AppOf(caller);
        if (string.IsNullOrEmpty(callerAppKey)) { return false; }

        var clean = StripNeuronPrefix(targetOrConnectionKey);
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        var canonicalKey = slash >= 0 && ConnectionKeys.Is(clean[(slash + 1)..]) ? clean[(slash + 1)..] : clean;
        var ownerBrain = TryExtractBrain(canonicalKey, out var extracted)
            ? extracted.Value
            : !string.IsNullOrWhiteSpace(brain) ? brain : string.Empty;
        if (string.IsNullOrEmpty(ownerBrain)) { return false; }

        var targets = new List<string> { canonicalKey };
        if (!string.Equals(canonicalKey, targetOrConnectionKey, StringComparison.Ordinal))
        {
            targets.Add(targetOrConnectionKey);
        }
        if (!canonicalKey.StartsWith(ownerBrain + "/", StringComparison.Ordinal) && !string.IsNullOrEmpty(canonicalKey) && TryParseOwnerBrain(ownerBrain, out var validOwner))
        {
            targets.Add(new DigitalBrain.NeuronId(validOwner, "connection", canonicalKey).FullKey());
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
    public async Task<bool> IsConnectionBoundAsync(Caller caller, string? targetOrConnectionKey)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(targetOrConnectionKey) || !IsConnectionTarget(targetOrConnectionKey)) { return false; }
        if (TryExtractBrain(targetOrConnectionKey, out var targetBrain))
        {
            if (await IsConnectionBoundAsync(targetBrain.Value, caller, targetOrConnectionKey)) { return true; }
            return !string.IsNullOrWhiteSpace(caller.Brain.Value) && !string.Equals(caller.Brain.Value, targetBrain.Value, StringComparison.OrdinalIgnoreCase)
                && await IsConnectionBoundAsync(caller.Brain.Value, caller, targetOrConnectionKey);
        }

        return !string.IsNullOrWhiteSpace(caller.Brain.Value)
            && await IsConnectionBoundAsync(caller.Brain.Value, caller, targetOrConnectionKey);
    }

    public bool IsConnectionBound(Caller caller, string? targetOrConnectionKey)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(targetOrConnectionKey) || !IsConnectionTarget(targetOrConnectionKey)) { return false; }
        if (TryExtractBrain(targetOrConnectionKey, out var targetBrain))
        {
            if (IsConnectionBoundInMemory(targetBrain.Value, caller, targetOrConnectionKey)) { return true; }
            return !string.IsNullOrWhiteSpace(caller.Brain.Value) && !string.Equals(caller.Brain.Value, targetBrain.Value, StringComparison.OrdinalIgnoreCase)
                && IsConnectionBoundInMemory(caller.Brain.Value, caller, targetOrConnectionKey);
        }

        return !string.IsNullOrWhiteSpace(caller.Brain.Value)
            && IsConnectionBoundInMemory(caller.Brain.Value, caller, targetOrConnectionKey);
    }

    private static bool IsConnectionTarget(string targetOrConnectionKey)
    {
        var clean = StripNeuronPrefix(targetOrConnectionKey);
        if (ConnectionKeys.Is(clean) || clean.StartsWith("connection/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && ConnectionKeys.Is(clean[(slash + 1)..]);
    }

    private bool IsConnectionBoundInMemory(string brain, Caller caller, string targetOrConnectionKey)
    {
        var callerAppKey = GrantRules.AppOf(caller);
        if (string.IsNullOrEmpty(callerAppKey)) { return false; }
        var clean = StripNeuronPrefix(targetOrConnectionKey);
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        var canonicalKey = slash >= 0 && ConnectionKeys.Is(clean[(slash + 1)..]) ? clean[(slash + 1)..] : clean;
        var ownerBrain = TryExtractBrain(canonicalKey, out var extracted)
            ? extracted.Value
            : !string.IsNullOrWhiteSpace(brain) ? brain : string.Empty;
        if (string.IsNullOrEmpty(ownerBrain)) { return false; }
        if (!_cache.TryGetValue(ownerBrain, out var cached)) { return false; }
        var principals = PrincipalSet.For(caller);
        var targets = new List<string> { canonicalKey };
        if (!string.Equals(canonicalKey, targetOrConnectionKey, StringComparison.Ordinal)) { targets.Add(targetOrConnectionKey); }
        if (!canonicalKey.StartsWith(ownerBrain + "/", StringComparison.Ordinal) && !string.IsNullOrEmpty(canonicalKey) && TryParseOwnerBrain(ownerBrain, out var validOwnerInMemory))
        {
            targets.Add(new DigitalBrain.NeuronId(validOwnerInMemory, "connection", canonicalKey).FullKey());
        }
        foreach (var target in targets)
        {
            if (GrantRules.IsGranted("use-via:" + callerAppKey, target, cached.Grants, principals, caller)) { return true; }
        }
        return false;
    }

    private static bool TryParseOwnerBrain(string ownerBrain, out DigitalBrain.BrainId brain)
    {
        if (DigitalBrain.BrainId.IsValid(ownerBrain))
        {
            brain = new DigitalBrain.BrainId(ownerBrain);
            return true;
        }
        if (ownerBrain is ['@', .. var sys] && DigitalBrain.BrainId.IsValid(sys))
        {
            brain = DigitalBrain.BrainId.System(sys);
            return true;
        }
        brain = default;
        return false;
    }

    public void BindFriendConnection(string connectionKey, string friendAppId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAppId);
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        if (!TryExtractBrain(normalizedKey, out var brain))
        {
            throw new ArgumentException($"Connection key '{connectionKey}' must include a valid brain prefix.", nameof(connectionKey));
        }

        var grant = Grant.Tuple($"brain:{brain.Value}", $"app:{normalizedFriend}", normalizedKey, $"use-via:{normalizedFriend}") with
        {
            Id = $"binding:{normalizedKey}:{normalizedFriend}",
        };
        AddGrant(brain.Value, grant);
    }

    public void UnbindFriendConnection(string connectionKey, string friendAppId)
    {
        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(friendAppId)) { return; }
        var normalizedKey = StripNeuronPrefix(connectionKey);
        var normalizedFriend = NormalizeAppId(friendAppId);
        if (!TryExtractBrain(normalizedKey, out var brain)) { return; }

        RemoveGrant(brain.Value, g => string.Equals(g.Id, $"binding:{normalizedKey}:{normalizedFriend}", StringComparison.Ordinal)
            || ((string.Equals(g.Target, normalizedKey, StringComparison.Ordinal) || string.Equals(g.TargetNeuronKey, normalizedKey, StringComparison.Ordinal))
                && (string.Equals(g.Grantee, $"app:{normalizedFriend}", StringComparison.Ordinal) || string.Equals(g.AppId, normalizedFriend, StringComparison.Ordinal))));
    }

    private static string NormalizeAppId(string appId)
        => AppPrincipal.AppKeyOf(appId.Trim()) ?? appId.Trim();

    internal static bool TryExtractBrain(string? keyOrApp, out DigitalBrain.BrainId brain)
    {
        brain = default;
        if (string.IsNullOrWhiteSpace(keyOrApp)) { return false; }
        var clean = StripNeuronPrefix(keyOrApp);
        if (clean.StartsWith("app:", StringComparison.Ordinal)) { clean = clean["app:".Length..]; }
        var isBrainPrefixed = clean.StartsWith("brain:", StringComparison.Ordinal);
        if (isBrainPrefixed) { clean = clean["brain:".Length..]; }
        if (ConnectionKeys.TryParse(clean, out var conn) && DigitalBrain.BrainId.IsValid(conn.BrainId))
        {
            brain = new DigitalBrain.BrainId(conn.BrainId);
            return true;
        }
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && ConnectionKeys.TryParse(clean[(slash + 1)..], out var nestedConn) && DigitalBrain.BrainId.IsValid(nestedConn.BrainId))
        {
            brain = new DigitalBrain.BrainId(nestedConn.BrainId);
            return true;
        }
        if (clean.StartsWith("connection/", StringComparison.OrdinalIgnoreCase)) { return false; }
        if (slash > 0 && DigitalBrain.BrainId.IsValid(clean[..slash]))
        {
            brain = new DigitalBrain.BrainId(clean[..slash]);
            return true;
        }
        if (isBrainPrefixed && DigitalBrain.BrainId.IsValid(clean))
        {
            brain = new DigitalBrain.BrainId(clean);
            return true;
        }
        return false;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        lock (_sync)
        {
            var grants = _cache.TryGetValue(brain, out var entry) ? entry.Grants.ToList() : new List<Grant>();
            grants.RemoveAll(g => string.Equals(g.Id, grant.Id, StringComparison.Ordinal)
                || (string.Equals(g.Owner, grant.Owner, StringComparison.Ordinal)
                    && string.Equals(g.Grantee, grant.Grantee, StringComparison.Ordinal)
                    && string.Equals(g.Target, grant.Target, StringComparison.Ordinal)
                    && string.Equals(g.Scope, grant.Scope, StringComparison.Ordinal)));
            grants.Add(grant);
            _cache[brain] = new CachedGrantsEntry(grants.ToArray(), (entry?.Version ?? 0) + 1, _clock.GetUtcNow());
        }
    }

    public void RemoveGrant(string brain, Func<Grant, bool> predicate)
    {
        if (string.IsNullOrWhiteSpace(brain)) { return; }
        lock (_sync)
        {
            if (_cache.TryGetValue(brain, out var entry))
            {
                var filtered = entry.Grants.Where(g => !predicate(g)).ToArray();
                _cache[brain] = new CachedGrantsEntry(filtered, entry.Version + 1, _clock.GetUtcNow());
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
