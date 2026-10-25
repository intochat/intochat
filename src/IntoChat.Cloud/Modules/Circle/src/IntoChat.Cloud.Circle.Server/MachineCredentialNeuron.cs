using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace IntoChat.Cloud.Circle.Server;

// What a redeemed invite grants one machine: the circle, its role and the key the machine proved it holds.
[GenerateSerializer, Alias("circle-server.enroll-grant")]
internal sealed record EnrollGrant(
    [property: Id(0)] string CircleId,
    [property: Id(1)] string CircleName,
    [property: Id(2)] string Role,
    [property: Id(3)] string? AccountId,
    [property: Id(4)] bool PendingAccount,
    [property: Id(5)] string MachineId,
    [property: Id(6)] string MachineName,
    [property: Id(7)] byte[] PublicKeySpki,
    [property: Id(8)] string Thumbprint,
    // True for a repeat of the same redeem (same machine and key within the invite repeat window).
    [property: Id(9)] bool Repeat);

// Raw tokens leave the neuron once, in this answer; only their SHA-256 hashes are kept.
[GenerateSerializer, Alias("circle-server.issued-tokens")]
internal sealed record IssuedTokens(
    [property: Id(0)] string CredentialId,
    [property: Id(1)] string AccessToken,
    [property: Id(2)] DateTimeOffset AccessExpiresAt,
    [property: Id(3)] string RefreshToken,
    [property: Id(4)] DateTimeOffset RefreshExpiresAt,
    [property: Id(5)] string CircleId,
    [property: Id(6)] string CircleName,
    [property: Id(7)] string Role,
    [property: Id(8)] bool PendingAccount);

[GenerateSerializer, Alias("circle-server.refresh-result")]
internal enum RefreshResult
{
    Rotated = 0,
    // Unknown, expired or revoked: refused without changing anything.
    Rejected = 1,
    // A refresh token that was already rotated away: the credential is revoked (theft detection).
    ReuseRevoked = 2,
}

[GenerateSerializer, Alias("circle-server.refresh-outcome")]
internal sealed record RefreshOutcome([property: Id(0)] RefreshResult Result, [property: Id(1)] IssuedTokens? Tokens = null);

// The machine behind a valid access token. Revoked is true only when the caller asked to see revoked credentials.
[GenerateSerializer, Alias("circle-server.machine-principal")]
internal sealed record MachinePrincipal(
    [property: Id(0)] string CredentialId,
    [property: Id(1)] string CircleId,
    [property: Id(2)] string CircleName,
    [property: Id(3)] string MachineId,
    [property: Id(4)] string Role,
    [property: Id(5)] string? AccountId,
    [property: Id(6)] bool PendingAccount,
    [property: Id(7)] byte[] PublicKeySpki,
    [property: Id(8)] bool Revoked);

[GenerateSerializer, Alias("circle-server.machine-credential-record")]
internal sealed class MachineCredentialRecord
{
    [Id(0)] public string CircleId { get; set; } = "";
    [Id(1)] public string CircleName { get; set; } = "";
    [Id(2)] public string MachineId { get; set; } = "";
    [Id(3)] public string MachineName { get; set; } = "";
    [Id(4)] public byte[] PublicKeySpki { get; set; } = [];
    [Id(5)] public string Thumbprint { get; set; } = "";
    [Id(6)] public string Role { get; set; } = "";
    [Id(7)] public string? AccountId { get; set; }
    [Id(8)] public bool PendingAccount { get; set; }
    [Id(9)] public long Generation { get; set; }
    [Id(10)] public string RefreshHash { get; set; } = "";
    [Id(11)] public DateTimeOffset RefreshExpiresAt { get; set; }
    [Id(12)] public string AccessHash { get; set; } = "";
    [Id(13)] public DateTimeOffset AccessExpiresAt { get; set; }
    [Id(14)] public bool Revoked { get; set; }
    [Id(15)] public DateTimeOffset EnrolledAt { get; set; }
    [Id(16)] public DateTimeOffset LastSeenAt { get; set; }
    // Refresh hashes rotated away; presenting one revokes the credential.
    [Id(17)] public string[] RotatedRefreshHashes { get; set; } = [];
    // The latest proof nonces of this credential, so a replay is caught whichever silo serves it.
    [Id(18)] public string[] RecentNonces { get; set; } = [];
}

// D-09: one machine's circle credential, keyed by its credential id (derived from circle and machine, so a repeated
// enrollment of the same machine keeps it). Access tokens live AccessLifetime; refresh tokens rotate on every use.
[Alias("circle-server.machine-credential")]
internal interface IMachineCredential : IGrainWithStringKey
{
    // Null when a repeat does not match the credential the first redeem created.
    Task<IssuedTokens?> Enroll(EnrollGrant grant);
    Task<RefreshOutcome> Refresh(string refreshToken);
    // The bound SPKI (public), or empty when there is no live credential.
    Task<byte[]> PublicKey();
    Task<MachinePrincipal?> Authenticate(string accessToken, bool includeRevoked);
    // False when this credential already used the nonce.
    Task<bool> UseNonce(string nonce);
    Task Revoke();
}

[GrainType("circle-server.machine-credential")]
internal sealed class MachineCredentialNeuron(
    [PersistentState("circle-server.machine-credential", CircleServerModule.GrainStore)] IPersistentState<MachineCredentialRecord> store,
    IOptions<CircleServerOptions> options)
    : Grain, IMachineCredential
{
    private const int RotatedKept = 64;
    private const int NoncesKept = 256;

    private MachineCredentialRecord Record => store.State;

    private DateTimeOffset Now => (ServiceProvider.GetKeyedService<TimeProvider>(CircleServerModule.ClockKey) ?? TimeProvider.System).GetUtcNow();

    public async Task<IssuedTokens?> Enroll(EnrollGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var now = Now;
        if (grant.Repeat)
        {
            if (!store.RecordExists || Record.Revoked || !string.Equals(Record.Thumbprint, grant.Thumbprint, StringComparison.Ordinal)) { return null; }
        }
        else
        {
            store.State = new MachineCredentialRecord
            {
                CircleId = grant.CircleId, CircleName = grant.CircleName, MachineId = grant.MachineId, MachineName = grant.MachineName,
                PublicKeySpki = grant.PublicKeySpki, Thumbprint = grant.Thumbprint, Role = grant.Role, AccountId = grant.AccountId,
                PendingAccount = grant.PendingAccount, EnrolledAt = now,
            };
        }
        // The earlier tokens stop working. None count as rotated: the machine enrolled again on purpose.
        Record.RotatedRefreshHashes = [];
        return await Issue(now);
    }

    public async Task<RefreshOutcome> Refresh(string refreshToken)
    {
        if (!store.RecordExists || Record.Revoked) { return new(RefreshResult.Rejected); }
        var hash = Hash(refreshToken);
        var now = Now;
        if (Matches(hash, Record.RefreshHash))
        {
            if (now >= Record.RefreshExpiresAt) { return new(RefreshResult.Rejected); }
            Record.RotatedRefreshHashes = [.. Record.RotatedRefreshHashes.Append(Record.RefreshHash).TakeLast(RotatedKept)];
            return new(RefreshResult.Rotated, await Issue(now));
        }
        if (Record.RotatedRefreshHashes.Any(rotated => Matches(hash, rotated)))
        {
            await Revoke();
            return new(RefreshResult.ReuseRevoked);
        }
        return new(RefreshResult.Rejected);
    }

    public Task<byte[]> PublicKey() => Task.FromResult(store.RecordExists && !Record.Revoked ? Record.PublicKeySpki : []);

    public Task<MachinePrincipal?> Authenticate(string accessToken, bool includeRevoked)
    {
        // A revoked credential keeps its last access hash so its machine can still read its own status.
        if (!store.RecordExists || (Record.Revoked && !includeRevoked)
            || !Matches(Hash(accessToken), Record.AccessHash) || Now >= Record.AccessExpiresAt)
        {
            return Task.FromResult<MachinePrincipal?>(null);
        }
        return Task.FromResult<MachinePrincipal?>(new MachinePrincipal(this.GetPrimaryKeyString(), Record.CircleId, Record.CircleName,
            Record.MachineId, Record.Role, Record.AccountId, Record.PendingAccount, Record.PublicKeySpki, Record.Revoked));
    }

    public async Task<bool> UseNonce(string nonce)
    {
        if (!store.RecordExists || Record.RecentNonces.Contains(nonce, StringComparer.Ordinal)) { return false; }
        Record.RecentNonces = [.. Record.RecentNonces.Append(nonce).TakeLast(NoncesKept)];
        Record.LastSeenAt = Now;
        await store.WriteStateAsync();
        return true;
    }

    public async Task Revoke()
    {
        if (!store.RecordExists || Record.Revoked) { return; }
        Record.Revoked = true;
        Record.RefreshHash = "";
        Record.RotatedRefreshHashes = [];
        await store.WriteStateAsync();
    }

    private async Task<IssuedTokens> Issue(DateTimeOffset now)
    {
        var id = this.GetPrimaryKeyString();
        var access = MachineTokens.New(id);
        var refresh = MachineTokens.New(id);
        Record.Generation++;
        Record.AccessHash = Hash(access);
        Record.AccessExpiresAt = now + options.Value.AccessLifetime;
        Record.RefreshHash = Hash(refresh);
        Record.RefreshExpiresAt = now + options.Value.RefreshLifetime;
        Record.LastSeenAt = now;
        await store.WriteStateAsync();
        return new IssuedTokens(id, access, Record.AccessExpiresAt, refresh, Record.RefreshExpiresAt,
            Record.CircleId, Record.CircleName, Record.Role, Record.PendingAccount);
    }

    private static string Hash(string token) => MachineProof.AccessTokenHash(token);

    private static bool Matches(string left, string right)
        => right.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}

// Opaque tokens: "{credentialId}.{32 random bytes, base64url}". The prefix only routes to the credential neuron,
// which compares the SHA-256 of the whole token.
internal static class MachineTokens
{
    public const int CredentialIdLength = 32;

    public static string New(string credentialId) => credentialId + "." + System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string CredentialIdFor(string circleId, string machineId)
        => System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(circleId + "\n" + machineId)))[..CredentialIdLength];

    public static bool TryCredentialId(string? token, out string credentialId)
    {
        credentialId = "";
        if (token is null || token.Length > 128 || token.IndexOf('.', StringComparison.Ordinal) != CredentialIdLength) { return false; }
        credentialId = token[..CredentialIdLength];
        return true;
    }
}
