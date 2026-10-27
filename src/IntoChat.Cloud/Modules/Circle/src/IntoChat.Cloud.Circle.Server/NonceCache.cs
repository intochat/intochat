using System.Collections.Concurrent;

namespace IntoChat.Cloud.Circle.Server;

// The silo's record of proof nonces (T-10-01). Entries live until the proof could no longer pass the skew check;
// each credential neuron also keeps its own latest nonces, so a replay sent to another replica is caught there.
internal sealed class NonceCache(TimeProvider clock) : INonceCache
{
    private const int SweepEvery = 1024;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private int _adds;

    public bool TryAdd(string jti, DateTimeOffset expires)
    {
        ArgumentNullException.ThrowIfNull(jti);
        var now = clock.GetUtcNow();
        if (Interlocked.Increment(ref _adds) % SweepEvery == 0) { Sweep(now); }
        while (true)
        {
            if (_seen.TryAdd(jti, expires)) { return true; }
            if (!_seen.TryGetValue(jti, out var held)) { continue; }
            if (held > now) { return false; }
            if (_seen.TryUpdate(jti, expires, held)) { return true; }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var entry in _seen.Where(entry => entry.Value <= now)) { _seen.TryRemove(entry); }
    }
}

// Wraps the silo cache for one verification and remembers the nonce the proof carried, so the credential neuron can
// record it too.
internal sealed class CapturingNonces(INonceCache inner) : INonceCache
{
    public string? Nonce { get; private set; }

    public bool TryAdd(string jti, DateTimeOffset expires)
    {
        Nonce = jti;
        return inner.TryAdd(jti, expires);
    }
}
