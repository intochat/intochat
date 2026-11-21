using System.Security.Cryptography;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Identity.Codes;

internal sealed class CodeIndexRecord
{
    [JsonPropertyName("circleBrainId")]
    public string Brain { get; set; } = "";

    [JsonPropertyName("inviteId")]
    public string Reference { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    public CodePurpose Purpose { get; set; } = CodePurpose.Legacy;

    public bool Spent { get; set; }
}

internal sealed class CodeKeyRecord
{
    public byte[] Key { get; set; } = [];
}

internal sealed record InviteCodeIsAvailable(CodeIndexRecord? Held) : Rule("circles.invite-code-available")
{
    public override bool Broken => Held is not null;
    public override string Message => "This invite code is already in use.";
}

internal sealed record InviteHmacKeyIsLongEnough(int Length) : Rule("circles.invite-hmac-key-length")
{
    public override bool Broken => Length < 32;
    public override string Message => $"{ICodeKey.Configuration} must be at least 32 bytes.";
}

// One plain neuron per code hash in the reserved system brain ("system/{hash}"). Keeps the stored neuron type
// "circles.invite-code" and wire member names so existing records load as Legacy without wiping state.
[Neuron(ICodeIndex.Type)]
internal sealed class CodeIndexNeuron : Neuron<CodeIndexRecord>, ICodeIndex
{
    private CodeIndexRecord? Record => State.ExpiresAt == default ? null : State;

    public async Task Bind(CodePurpose purpose, string brain, string reference, DateTimeOffset expiresAt)
    {
        await Check("Bind", new InviteCodeIsAvailable(Record));
        var next = new CodeIndexRecord
        {
            Brain = brain,
            Reference = reference,
            ExpiresAt = expiresAt,
            Purpose = purpose,
            Spent = false,
        };
        await Store(next);
    }

    public Task<CodeTarget?> Resolve(CodePurpose purpose)
    {
        if (Record is not { } record
            || purpose == CodePurpose.Legacy
            || record.Purpose == CodePurpose.Legacy
            || record.Purpose != purpose
            || record.Spent
            || Context.Time.GetUtcNow() >= record.ExpiresAt)
        {
            return Task.FromResult<CodeTarget?>(null);
        }

        return Task.FromResult<CodeTarget?>(new CodeTarget(new BrainId(record.Brain), record.Reference, record.ExpiresAt));
    }

    // Finds the owning brain for a non-legacy purpose even after expiry or spend so the target neuron can answer
    // Repeat (within its idempotency window) or Expired (410) instead of collapsing them to Rejected.
    public Task<CodeTarget?> Lookup(CodePurpose purpose)
    {
        if (Record is not { } record
            || purpose == CodePurpose.Legacy
            || record.Purpose == CodePurpose.Legacy
            || record.Purpose != purpose)
        {
            return Task.FromResult<CodeTarget?>(null);
        }

        return Task.FromResult<CodeTarget?>(new CodeTarget(new BrainId(record.Brain), record.Reference, record.ExpiresAt));
    }

    public async Task Spend()
    {
        if (Record is not { } record || record.Spent) { return; }
        await Store(new CodeIndexRecord
        {
            Brain = record.Brain,
            Reference = record.Reference,
            ExpiresAt = record.ExpiresAt,
            Purpose = record.Purpose,
            Spent = true,
        });
    }
}

// The deployment's invite HMAC key: IntoChatOS:Circles:InviteHmacKey (base64, at least 32 bytes) when configured,
// otherwise 32 random bytes created on first use and kept in this plain neuron's record ("system/main").
[Neuron(ICodeKey.Type)]
internal sealed class CodeKeyNeuron(IConfiguration? configuration = null) : Neuron<CodeKeyRecord>, ICodeKey
{
    private CodeKeyRecord Record => State;

    public async Task<byte[]> Get()
    {
        if (configuration?[ICodeKey.Configuration] is { Length: > 0 } configured)
        {
            var key = Convert.FromBase64String(configured);
            await Check("Get", new InviteHmacKeyIsLongEnough(key.Length));
            return key;
        }
        if (Record.Key.Length == 0)
        {
            var next = new CodeKeyRecord { Key = RandomNumberGenerator.GetBytes(32) };
            await Store(next);
            return next.Key;
        }
        return Record.Key;
    }
}
