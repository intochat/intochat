using System.Collections.Concurrent;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Identity.Grants;

public interface IEdgeGrantsCache
{
    Task<IReadOnlyList<Grant>> GetGrantsAsync(string brain, CancellationToken ct = default);
    Task<bool> IsGrantedAsync(string brain, CapabilityCaller caller, string semanticTypeId, string? targetNeuron = null, CancellationToken ct = default);
    Task<bool> IsConnectionBoundAsync(string brain, CapabilityCaller caller, string targetOrConnectionKey, CancellationToken ct = default);
    void Invalidate(string brain);
    void Clear();
}

public sealed class EdgeGrantsCache : IEdgeGrantsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    public static EdgeGrantsCache Current { get; set; } = new();

    private readonly ConcurrentDictionary<string, CachedGrantsEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider? _services;
    private readonly TimeProvider _clock;

    private sealed record CachedGrantsEntry(Grant[] Grants, long Version, DateTimeOffset CachedAt);

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
                _cache[brain] = entry;
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

        var candidate = StripNeuronPrefix(targetOrConnectionKey);
        var slash = candidate.IndexOf('/', StringComparison.Ordinal);
        var unqualifiedCandidate = slash >= 0 ? candidate[(slash + 1)..] : candidate;

        return grants.Any(g => !g.Revoked
            && (string.Equals(g.AppId, callerApp, StringComparison.Ordinal)
                || string.Equals(g.Grantee, $"app:{callerApp}", StringComparison.Ordinal)
                || string.Equals(g.Grantee, caller.PrincipalId, StringComparison.Ordinal)
                || string.Equals(g.Grantee, $"brain:{caller.BrainId}", StringComparison.Ordinal))
            && (MatchesTarget(g.Target, candidate) || MatchesTarget(g.Target, unqualifiedCandidate)
                || MatchesTarget(g.TargetNeuronKey, candidate) || MatchesTarget(g.TargetNeuronKey, unqualifiedCandidate)));
    }

    private static string ExtractBrain(string keyOrApp)
    {
        var clean = StripNeuronPrefix(keyOrApp);
        if (clean.StartsWith("app:", StringComparison.Ordinal)) { clean = clean["app:".Length..]; }
        if (clean.StartsWith("brain:", StringComparison.Ordinal)) { clean = clean["brain:".Length..]; }
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? clean[..slash] : "local";
    }

    public void Invalidate(string brain)
    {
        if (!string.IsNullOrWhiteSpace(brain))
        {
            _cache.TryRemove(brain, out _);
        }
    }

    public void AddGrant(string brain, Grant grant)
    {
        var b = string.IsNullOrWhiteSpace(brain) ? "local" : brain;
        var grants = _cache.TryGetValue(b, out var entry) ? entry.Grants.ToList() : new List<Grant>();
        grants.RemoveAll(g => string.Equals(g.Id, grant.Id, StringComparison.Ordinal)
            || (string.Equals(g.Owner, grant.Owner, StringComparison.Ordinal)
                && string.Equals(g.Grantee, grant.Grantee, StringComparison.Ordinal)
                && string.Equals(g.Target, grant.Target, StringComparison.Ordinal)
                && string.Equals(g.Scope, grant.Scope, StringComparison.Ordinal)));
        grants.Add(grant);
        _cache[b] = new CachedGrantsEntry(grants.ToArray(), (entry?.Version ?? 0) + 1, _clock.GetUtcNow());
    }

    public void RemoveGrant(string brain, Func<Grant, bool> predicate)
    {
        var b = string.IsNullOrWhiteSpace(brain) ? "local" : brain;
        if (_cache.TryGetValue(b, out var entry))
        {
            var filtered = entry.Grants.Where(g => !predicate(g)).ToArray();
            _cache[b] = new CachedGrantsEntry(filtered, entry.Version + 1, _clock.GetUtcNow());
        }
    }

    public void SetGrantsForTest(string brain, Grant[] grants, long version = 1)
    {
        _cache[brain] = new CachedGrantsEntry(grants, version, _clock.GetUtcNow());
    }

    public void Clear() => _cache.Clear();

    private static string StripNeuronPrefix(string value)
        => value.StartsWith("neuron://", StringComparison.Ordinal) ? value["neuron://".Length..] : value;

    private static bool MatchesTarget(string? grantTarget, string candidate)
    {
        if (string.IsNullOrWhiteSpace(grantTarget)) { return false; }
        var normalized = StripNeuronPrefix(grantTarget);
        if (string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && string.Equals(normalized[(slash + 1)..], candidate, StringComparison.OrdinalIgnoreCase);
    }
}
