using System.Collections.Concurrent;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// Write-through, fail-closed per-owner redaction cache implementing <see cref="ISecretValueSource"/>.
/// Caches secret values of length <c>&gt;= SecretRedactor.MinimumLength</c> (8 chars), sorted longest-first,
/// with a bounded TTL (default 5 minutes). Every vault mutation updates or invalidates the owner's entry immediately.
/// App principals are unconditionally refused (zero-token auth enforcement), and cold-cache backend failures propagate
/// fail-closed so unredacted output is never leaked.
/// </summary>
public sealed class CachingSecretValueSource : ISecretValueSource
{
    /// <summary>Default cache entry time-to-live (5 minutes).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Func<string, CancellationToken, ValueTask<IReadOnlyList<string>>> _loader;
    private readonly ICallerAmbient _callers;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly Func<Caller, bool>? _extraAppCheck;
    private readonly ConcurrentDictionary<string, CachedValues> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a caching secret value source backed by <paramref name="loader"/>.</summary>
    public CachingSecretValueSource(
        Func<string, CancellationToken, ValueTask<IReadOnlyList<string>>> loader,
        ICallerAmbient callers,
        TimeProvider? clock = null,
        TimeSpan? ttl = null,
        Func<Caller, bool>? extraAppCheck = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(callers);
        var resolvedTtl = ttl ?? DefaultTtl;
        if (resolvedTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Redaction cache TTL must be positive.");
        }

        _loader = loader;
        _callers = callers;
        _clock = clock ?? TimeProvider.System;
        _ttl = resolvedTtl;
        _extraAppCheck = extraAppCheck;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
    {
        if (_callers.Current is not { } caller)
        {
            return [];
        }

        if (VaultPolicy.IsAppCaller(caller) || (_extraAppCheck is not null && _extraAppCheck(caller)))
        {
            throw new UnauthorizedAccessException(
                "Zero-token auth enforcement: sandbox and app principals cannot read raw vault secret values directly.");
        }

        if (VaultPolicy.RedactionOwner(caller) is not { } owner)
        {
            return [];
        }

        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(owner, out var cached) && now < cached.ExpiresAt)
        {
            return cached.Values;
        }

        // Fail-closed: if the backend is unreachable when the cache is cold or expired, let the exception propagate
        // so the caller fails closed rather than emitting unredacted output.
        var loaded = await _loader(owner, cancellationToken).ConfigureAwait(false);
        var normalized = Normalize(loaded);
        _cache[owner] = new CachedValues(normalized, now + _ttl);
        return normalized;
    }

    /// <summary>
    /// Write-through update when a secret is stored for <paramref name="owner"/>.
    /// If the owner's cache entry is warm, adds the value immediately (if it meets the minimum redaction length);
    /// otherwise leaves it cold so the next read fetches the full set from the vault.
    /// </summary>
    public void NotifySet(string owner, string value)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        if (value is not { Length: >= SecretRedactor.MinimumLength })
        {
            _cache.TryRemove(owner, out _);
            return;
        }

        var now = _clock.GetUtcNow();
        _cache.AddOrUpdate(
            owner,
            _ => new CachedValues([], DateTimeOffset.MinValue),
            (_, existing) =>
            {
                if (now >= existing.ExpiresAt)
                {
                    return new CachedValues([], DateTimeOffset.MinValue);
                }

                if (existing.Values.Contains(value, StringComparer.Ordinal))
                {
                    return new CachedValues(existing.Values, now + _ttl);
                }

                var updated = new List<string>(existing.Values.Count + 1);
                updated.AddRange(existing.Values);
                updated.Add(value);
                updated.Sort((left, right) => right.Length.CompareTo(left.Length));
                return new CachedValues(updated, now + _ttl);
            });
    }

    /// <summary>Invalidates <paramref name="owner"/>'s cached values when a secret is removed.</summary>
    public void NotifyRemove(string owner)
    {
        if (!string.IsNullOrWhiteSpace(owner))
        {
            _cache.TryRemove(owner, out _);
        }
    }

    /// <summary>Invalidates <paramref name="owner"/>'s cached values.</summary>
    public void Invalidate(string owner) => NotifyRemove(owner);

    /// <summary>Clears all cached entries.</summary>
    public void Clear() => _cache.Clear();

    private static List<string> Normalize(IReadOnlyList<string> raw)
    {
        if (raw.Count == 0)
        {
            return [];
        }

        var filtered = new List<string>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in raw)
        {
            if (value is { Length: >= SecretRedactor.MinimumLength } && seen.Add(value))
            {
                filtered.Add(value);
            }
        }

        filtered.Sort((left, right) => right.Length.CompareTo(left.Length));
        return filtered;
    }

    private sealed record CachedValues(IReadOnlyList<string> Values, DateTimeOffset ExpiresAt);
}
