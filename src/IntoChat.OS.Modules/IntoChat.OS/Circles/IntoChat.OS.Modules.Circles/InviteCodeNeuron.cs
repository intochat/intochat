using System.Security.Cryptography;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Circles;

// D-10, the InviteTokenNeuron pattern: one neuron per code hash names the circle brain and invite it opens. The key
// is the HMAC of the code; the code itself is never stored.
[Alias("circles.invite-code"), DefaultGrainType("circles.invite-code")]
[Internal]
internal interface IInviteCode : INeuron
{
    Task Bind(string circleBrainId, string inviteId, DateTimeOffset expiresAt);
    Task<RedeemOutcome> Redeem(RedeemInvite request);

    static string Key(string codeHash) => $"circle-invite-code/{codeHash}";
}

[GenerateSerializer, Alias("circles.invite-code-record")]
internal sealed class InviteCodeRecord
{
    [Id(0)] public string CircleBrainId { get; set; } = "";
    [Id(1)] public string InviteId { get; set; } = "";
    [Id(2)] public DateTimeOffset ExpiresAt { get; set; }
}

[GrainType("circles.invite-code")]
internal sealed class InviteCodeNeuron(
    [PersistentState("circles.invite-code", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<InviteCodeRecord> store)
    : Neuron<InviteCodeRecord>(store), IInviteCode
{
    public async Task Bind(string circleBrainId, string inviteId, DateTimeOffset expiresAt)
    {
        // 60 random bits: a collision is refused rather than overwritten.
        if (Exists) { throw new InvalidOperationException("This invite code is already in use."); }
        await SaveQuietly(new InviteCodeRecord { CircleBrainId = circleBrainId, InviteId = inviteId, ExpiresAt = expiresAt });
    }

    public Task<RedeemOutcome> Redeem(RedeemInvite request)
        => !Exists ? Task.FromResult(RedeemOutcome.Rejected)
            : GrainFactory.GetGrain<ICircleInvites>(ICircleInvites.Key(Snapshot.CircleBrainId)).Redeem(Snapshot.InviteId, request);
}

// The deployment's invite HMAC key: IntoChatOS:Circles:InviteHmacKey (base64, at least 32 bytes) when configured,
// otherwise 32 random bytes created on first use and kept in this neuron's record (never exported with a brain).
[Alias("circles.invite-key"), DefaultGrainType("circles.invite-key")]
[Internal]
internal interface IInviteKey : INeuron
{
    const string Key = "circle-invite-key";
    const string Configuration = "IntoChatOS:Circles:InviteHmacKey";

    Task<byte[]> Get();
}

[GenerateSerializer, Alias("circles.invite-key-record")]
internal sealed class InviteKeyRecord
{
    [Id(0)] public byte[] Key { get; set; } = [];
}

[GrainType("circles.invite-key")]
internal sealed class InviteKeyNeuron(
    [PersistentState("circles.invite-key", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<InviteKeyRecord> store)
    : Neuron<InviteKeyRecord>(store), IInviteKey
{
    public async Task<byte[]> Get()
    {
        if (ServiceProvider.GetService<IConfiguration>()?[IInviteKey.Configuration] is { Length: > 0 } configured)
        {
            var key = Convert.FromBase64String(configured);
            return key.Length >= 32 ? key : throw new InvalidOperationException($"{IInviteKey.Configuration} must be at least 32 bytes.");
        }
        if (!Exists || Snapshot.Key.Length == 0) { await SaveQuietly(new InviteKeyRecord { Key = RandomNumberGenerator.GetBytes(32) }); }
        return Snapshot.Key;
    }
}

internal sealed class CircleInviteRedeemer(IGrainFactory grains) : ICircleInviteRedeemer
{
    public async Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName)
    {
        if (!InviteCode.TryNormalize(code, out var normalized)) { return RedeemOutcome.Rejected; }
        var hash = InviteCode.Hash(normalized, await grains.GetGrain<IInviteKey>(IInviteKey.Key).Get());
        return await grains.GetGrain<IInviteCode>(IInviteCode.Key(hash)).Redeem(new RedeemInvite(hash, machineId, machineKeyThumbprint, machineName));
    }
}
