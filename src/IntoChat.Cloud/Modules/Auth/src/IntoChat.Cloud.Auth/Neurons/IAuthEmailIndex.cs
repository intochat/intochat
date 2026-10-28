using DigitalBrain.Access;
using IntoChat.Cloud.Mail;

namespace IntoChat.Cloud.Auth.Neurons;

// Global email uniqueness lives here: one entry per email hash in a system shard.
[DigitalBrain.Contracts.NeuronAlias("auth.email-index"), DigitalBrain.Contracts.DefaultNeuronType("auth.email-index")]
public interface IAuthEmailIndex
{
    const string Type = "auth.email-index";

    static string Key(string emailHash) => $"{SystemShard.BrainOf(emailHash)}/auth/email/{emailHash}";

    [Internal]
    Task<AuthEmailIndexState> Get();

    // Records (or overwrites) an unverified sign-up; a verified entry is never touched. Returns the entry's principal id.
    [Internal]
    Task<string?> SetPending(PendingSignUp pending);

    [Internal]
    Task<bool> MarkVerified(string principalId, string accountId, string brainId);

    [Internal]
    Task ClearPending();

    [Internal]
    Task<bool> ReserveForChange(string principalId, string accountId, string brainId, string email, string previousEmailHash);

    [Internal]
    Task<bool> CommitChange(string principalId, string codeId);

    [Internal]
    Task<bool> BindChangeCode(string principalId, string codeId);

    [Internal]
    Task<bool> RebindChangeCode(string principalId, string previousCodeId, string codeId);

    [Internal]
    Task<bool> ProveChange(string principalId, string codeId);

    [Internal]
    Task CancelChange(string principalId, string codeId);

    [Internal]
    Task Release(string principalId);

    [Internal]
    Task<AuthEmailIndexState?> PrepareExternal(string email, string displayName, string language);

    [Internal]
    Task<bool> LinkGoogle(string principalId, string sub);
}

public sealed record PendingSignUp(
    string Email,
    string PasswordHash,
    string DisplayName,
    string Language,
    DateTimeOffset ConsentAt);
