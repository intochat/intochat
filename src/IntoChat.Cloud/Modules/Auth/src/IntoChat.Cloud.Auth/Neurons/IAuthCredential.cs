using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.Cloud.Mail;

namespace IntoChat.Cloud.Auth.Neurons;

// A principal's credentials, keyed by principal so they survive a brain switch (CV-2, R7).
[DigitalBrain.Contracts.NeuronAlias("auth.credential"), DigitalBrain.Contracts.DefaultNeuronType("auth.credential")]
public interface IAuthCredential
{
    const string Type = "auth.credential";

    static string Key(string principalId) => $"{SystemShard.BrainOf(principalId)}/auth/credential/{principalId}";

    [Internal]
    Task SetPassword(string passwordHash);

    [Internal]
    Task<CredentialFlags> GetFlags();

    [Internal]
    Task<PasswordCheck> CheckPassword(string password);

    [Internal]
    Task RotatePassword(string passwordHash);

    [Internal]
    Task<string?> GetEmailHash();

    [Internal]
    Task<bool> SetEmailHash(string emailHash);

    [Internal]
    Task<bool> TryMoveEmailHash(string expectedHash, string nextHash);

    [Internal]
    Task<bool> BeginTotp(SealedSecret pending);

    [Internal]
    Task<string[]?> ConfirmTotp(string code);

    [Internal]
    Task<bool> VerifyTotp(string code);

    [Internal]
    Task<bool> UseRecovery(string code);

    [Internal]
    Task DisableTotp();

    [Internal]
    Task<string[]> RegenerateRecovery();
}

public sealed record PasswordCheck(
    bool Ok,
    bool Locked,
    DateTimeOffset? LockedUntil);

public sealed record CredentialFlags(
    bool HasPassword,
    bool HasTotp);
