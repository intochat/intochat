using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.Connections.OAuth;

internal sealed record PendingDeviceFlow(
    string FlowId,
    string BrainId,
    string ConnectionKey,
    string AuthMethodId,
    string? Target,
    string DeviceCode,
    string ClientId,
    string TokenEndpoint,
    string[] Scopes,
    int IntervalSeconds,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset NextAllowedPollUtc)
{
#pragma warning disable CA1822
    private bool PrintMembers(StringBuilder builder) => builder.Append("<redacted>") is not null;
#pragma warning restore CA1822
}

// Bounded, TTL-expiring in-memory tracker for active RFC 8628 device authorization flows.
// Bound strictly to the owning BrainId so cross-brain polling is rejected.
internal sealed class OAuthDeviceRequests(IOptions<ConnectionsOptions> options, TimeProvider clock)
{
    private readonly Dictionary<string, PendingDeviceFlow> _pending = new(StringComparer.Ordinal);

    public string Begin(
        string brainId,
        string connectionKey,
        string authMethodId,
        string? target,
        string deviceCode,
        string clientId,
        string tokenEndpoint,
        string[] scopes,
        int intervalSeconds,
        DateTimeOffset expiresAtUtc)
    {
        lock (_pending)
        {
            var now = clock.GetUtcNow();
            PruneExpiredLocked(now);
            var capacity = Math.Max(1, options.Value.PendingLoginCapacity);
            if (_pending.Count >= capacity)
            {
                throw new InvalidOperationException("Too many pending device authorizations. Wait for an existing request to expire.");
            }

            var flowId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _pending.Add(flowId, new PendingDeviceFlow(
                flowId,
                brainId,
                connectionKey,
                authMethodId,
                target,
                deviceCode,
                clientId,
                tokenEndpoint,
                scopes,
                intervalSeconds,
                expiresAtUtc,
                NextAllowedPollUtc: now));
            return flowId;
        }
    }

    public bool TryGet(string flowId, string brainId, out PendingDeviceFlow? flow, out bool expired, out bool wrongBrain)
    {
        flow = null;
        expired = false;
        wrongBrain = false;
        if (string.IsNullOrWhiteSpace(flowId))
        {
            return false;
        }

        lock (_pending)
        {
            if (!_pending.TryGetValue(flowId, out var existing))
            {
                return false;
            }

            if (!string.Equals(existing.BrainId, brainId, StringComparison.Ordinal))
            {
                wrongBrain = true;
                return false;
            }

            if (existing.ExpiresAtUtc <= clock.GetUtcNow())
            {
                _pending.Remove(flowId);
                expired = true;
                return false;
            }

            flow = existing;
            return true;
        }
    }

    public void UpdatePollSchedule(string flowId, string brainId, int intervalSeconds, DateTimeOffset nextAllowedPollUtc)
    {
        lock (_pending)
        {
            if (_pending.TryGetValue(flowId, out var existing)
                && string.Equals(existing.BrainId, brainId, StringComparison.Ordinal))
            {
                _pending[flowId] = existing with
                {
                    IntervalSeconds = intervalSeconds,
                    NextAllowedPollUtc = nextAllowedPollUtc,
                };
            }
        }
    }

    public bool TryConsume(string flowId, string brainId, out PendingDeviceFlow? flow)
    {
        flow = null;
        if (string.IsNullOrWhiteSpace(flowId))
        {
            return false;
        }

        lock (_pending)
        {
            if (!_pending.TryGetValue(flowId, out var existing)
                || !string.Equals(existing.BrainId, brainId, StringComparison.Ordinal))
            {
                return false;
            }

            _pending.Remove(flowId);
            if (existing.ExpiresAtUtc <= clock.GetUtcNow())
            {
                return false;
            }

            flow = existing;
            return true;
        }
    }

    public void Remove(string flowId, string brainId)
    {
        if (string.IsNullOrWhiteSpace(flowId))
        {
            return;
        }

        lock (_pending)
        {
            if (_pending.TryGetValue(flowId, out var existing)
                && string.Equals(existing.BrainId, brainId, StringComparison.Ordinal))
            {
                _pending.Remove(flowId);
            }
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        var expired = _pending.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray();
        foreach (var key in expired)
        {
            _pending.Remove(key);
        }
    }
}
