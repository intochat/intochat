using DigitalBrain.Access;
using IntoChat.OS;

namespace IntoChat.Cloud.Auth.Neurons;

// The sessions table of one principal (W4 D11, R7). The session id itself never reaches the neuron (CV-3): callers pass its
// keyed hash (AuthHashing.SidHash); rows are listed by an opaque handle.
[DigitalBrain.Contracts.NeuronAlias("auth.sessions"), DigitalBrain.Contracts.DefaultNeuronType("auth.sessions")]
public interface IAuthSessions
{
    const string Type = "auth.sessions";

    static string Key(string principalId) => $"{NeuronKeys.SystemShard(principalId)}/auth/sessions/{principalId}";

    [Internal]
    Task<SessionCreated> Create(string sidHash, string handle, string label, TimeSpan lifetime);

    // Creates the row and remembers the member it was issued for, so identity@1 Validate can answer it.
    [Internal]
    Task<SessionCreated> Issue(SessionOpen open);

    // The member of the live row for the hash; null when it is unknown, revoked or expired.
    [Internal]
    Task<DigitalBrain.Capabilities.Identity.Member?> Find(string sidHash);

    // Names the device a row was opened from (the issuer port knows no HTTP request).
    [Internal]
    Task Describe(string sidHash, string label);

    [Internal]
    Task<bool> Check(string sidHash);

    [Internal]
    Task<SessionView[]> List(string? currentSidHash);

    [Internal]
    Task<bool> RevokeByHandle(string handle);

    [Internal]
    Task<int> RevokeOthers(string currentSidHash);

    [Internal]
    Task RevokeCurrent(string sidHash);

    [Internal]
    Task<int> RevokeAll();
}

public sealed record SessionCreated(
    string Handle,
    DateTimeOffset ExpiresAt);

public sealed record SessionView(
    string Handle,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool Current);

public sealed record SessionOpen(string SidHash, string Handle, string Label, TimeSpan Lifetime, DigitalBrain.Capabilities.Identity.Member Member);
