using System.Security.Cryptography;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Circles.Rules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Circles;

// D-10, the InviteTokenNeuron pattern: one neuron per code hash names the circle brain and invite it opens. The key
// is the HMAC of the code; the code itself is never stored. A plain neuron (06-08) of the system brain, "system/{hash}"
// (a cluster-scoped neuron lives in the reserved system brain, R4-19; clean break).
[DigitalBrain.Contracts.NeuronAlias("circles.invite-code"), DigitalBrain.Contracts.DefaultNeuronType(Type)]
[Internal]
internal interface IInviteCode
{
    const string Type = "circles.invite-code";

    Task Bind(string circleBrainId, string inviteId, DateTimeOffset expiresAt);
    Task<RedeemOutcome> Redeem(RedeemInvite request);

    static string Key(string codeHash) => $"system/{codeHash}";

}

internal sealed class InviteCodeRecord
{
    public string CircleBrainId { get; set; } = "";
    public string InviteId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

[DigitalBrain.Neuron(IInviteCode.Type)]
internal sealed class InviteCodeNeuron : DigitalBrain.Neuron, IInviteCode
{
    private InviteCodeRecord? record;

    protected override async Task OnActivate(CancellationToken cancellationToken) => record = await Context.State.Load<InviteCodeRecord>();

    public async Task Bind(string circleBrainId, string inviteId, DateTimeOffset expiresAt)
    {
        // 60 random bits: a collision is refused rather than overwritten.
        await Check("Bind", new InviteCodeIsAvailable(record is null));
        var next = new InviteCodeRecord { CircleBrainId = circleBrainId, InviteId = inviteId, ExpiresAt = expiresAt };
        await Context.State.Save(next, []);
        record = next;
    }

    public Task<RedeemOutcome> Redeem(RedeemInvite request)
    {
        return record is null ? Task.FromResult(RedeemOutcome.Rejected)
            // The circle brain's invites, reached through the host seam (a neuron's own Get stays in its brain).
            : SiloNeurons.Get<ICircleInvites>(Context.Services, ICircleInvites.Key(record.CircleBrainId)).Redeem(record.InviteId, request);
    }
}

// The deployment's invite HMAC key: IntoChatOS:Circles:InviteHmacKey (base64, at least 32 bytes) when configured,
// otherwise 32 random bytes created on first use and kept in this neuron's record (never exported with a brain).
// A plain neuron (06-08), the system brain's singleton "system/main" (R4-19; clean break: a new key, so codes minted before the
// break no longer redeem).
[DigitalBrain.Contracts.NeuronAlias("circles.invite-key"), DigitalBrain.Contracts.DefaultNeuronType(Type)]
[Internal]
internal interface IInviteKey
{
    const string Type = "circles.invite-key";
    const string Key = "system/main";
    const string Configuration = "IntoChatOS:Circles:InviteHmacKey";

    Task<byte[]> Get();
}

internal sealed class InviteKeyRecord
{
    public byte[] Key { get; set; } = [];
}

[DigitalBrain.Neuron(IInviteKey.Type)]
internal sealed class InviteKeyNeuron : DigitalBrain.Neuron, IInviteKey
{
    private InviteKeyRecord record = new();

    protected override async Task OnActivate(CancellationToken cancellationToken) => record = await Context.State.Load<InviteKeyRecord>() ?? new();

    public async Task<byte[]> Get()
    {
        if (Context.Services.GetService<IConfiguration>()?[IInviteKey.Configuration] is { Length: > 0 } configured)
        {
            var key = Convert.FromBase64String(configured);
            await Check("Get", new InviteHmacKeyIsLongEnough(key.Length));
            return key;
        }
        if (record.Key.Length == 0)
        {
            var next = new InviteKeyRecord { Key = RandomNumberGenerator.GetBytes(32) };
            await Context.State.Save(next, []);
            record = next;
        }
        return record.Key;
    }
}

internal sealed class CircleInviteRedeemer(IServiceProvider plain) : ICircleInviteRedeemer
{
    public async Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName)
    {
        if (!InviteCode.TryNormalize(code, out var normalized)) { return RedeemOutcome.Rejected; }
        var hash = InviteCode.Hash(normalized, await SiloNeurons.Get<IInviteKey>(plain, IInviteKey.Key).Get());
        return await SiloNeurons.Get<IInviteCode>(plain, IInviteCode.Key(hash)).Redeem(new RedeemInvite(hash, machineId, machineKeyThumbprint, machineName));
    }
}
