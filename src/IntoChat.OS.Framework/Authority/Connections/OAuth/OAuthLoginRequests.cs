using System.Security.Cryptography;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Connections.OAuth;

// OwnerPrincipal and OwnerBrain are captured from the stamped caller at Begin and checked again at the callback; they are data,
// never a caller: the callback re-establishes the person through EdgeAuthority after that check.
internal sealed record PendingLogin(string ConnectionKey, string AuthMethodId, string CodeVerifier, string? Target, string OwnerPrincipal, string OwnerBrain, DateTimeOffset ExpiresAt);

// A ten-minute capability for an in-flight browser redirect. Process memory on purpose: losing
// it on restart costs the user one more click and can never connect the wrong brain.
// Pending logins are per process; the OS host is single-replica (IntoChatOSDeployment
// MinReplicas = MaxReplicas = 1), so a callback reaching another replica would fail closed.
internal sealed class OAuthLoginRequests(IOptions<ConnectionsOptions> options, TimeProvider clock)
{
    private readonly Dictionary<string, PendingLogin> _pending = new(StringComparer.Ordinal);

    public (string State, string CodeVerifier) Begin(string connectionKey, string authMethodId, string? target, string ownerPrincipal, string ownerBrain)
    {
        lock (_pending)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in _pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) { _pending.Remove(expired); }
            if (_pending.Count >= options.Value.PendingLoginCapacity)
            { throw new InvalidOperationException("Too many pending logins. Wait for an existing request to expire."); }
            var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _pending.Add(state, new(connectionKey, authMethodId, verifier, target, ownerPrincipal, ownerBrain, now + options.Value.LoginLifetime));
            return (state, verifier);
        }
    }

    public bool TryClaim(string? state, out PendingLogin login)
    {
        login = null!;
        lock (_pending)
        {
            if (state is null || !_pending.TryGetValue(state, out var pending) || pending.ExpiresAt <= clock.GetUtcNow()) { return false; }
            _pending.Remove(state);
            login = pending;
            return true;
        }
    }
}
