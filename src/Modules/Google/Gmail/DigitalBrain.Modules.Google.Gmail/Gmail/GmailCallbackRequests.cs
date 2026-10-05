using System.Security.Cryptography;
namespace DigitalBrain.Google.Gmail;
internal sealed record GmailCallbackTarget(string AccountId, string BrainId, string ConnectionId);
internal sealed class GmailCallbackRequests(TimeProvider clock)
{
    private readonly Dictionary<string, (string Principal, GmailCallbackTarget Target, DateTimeOffset Expires)> pending = new();
    internal string Begin(string principal, string accountId, string brainId, string connectionId)
    {
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        lock (pending) { foreach (var expired in pending.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) { pending.Remove(expired); } pending.Add(state, (principal, new(accountId, brainId, connectionId), clock.GetUtcNow().AddMinutes(10))); }
        return state;
    }
    internal bool TryClaim(string? state, string principal, string accountId, out GmailCallbackTarget target)
    {
        target = null!;
        lock (pending) { if (state is null || !pending.TryGetValue(state, out var request) || request.Principal != principal || request.Target.AccountId != accountId || request.Expires <= clock.GetUtcNow()) { return false; } pending.Remove(state); target = request.Target; return true; }
    }
}
