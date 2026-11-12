using DigitalBrain.Capabilities.Secrets;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Services;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neurons.Neuron("auth.credential")]
internal sealed class AuthCredentialNeuron(
    TimeProvider clock,
    AuthModuleOptions options,
    AuthSecrets secrets,
    AuthHashing hashing) : AuthNeuron<AuthCredentialState>, IAuthCredential
{
    private readonly PasswordHasher hasher = new(options.PasswordIterations);
    private string? dummyHash;

    // The principal this credential belongs to: the key is "auth/credential/{principalId}" in the system shard brain.
    private string PrincipalId => Id.Key["auth/credential/".Length..];

    public async Task<PasswordCheck> CheckPassword(string password)
    {
        var current = Snapshot;
        var verification = hasher.Verify(password, current.PasswordHash ?? (dummyHash ??= hasher.Hash("dummy authentication password")));
        var now = clock.GetUtcNow();
        if (current.LockedUntil > now)
        {
            return new PasswordCheck(false, true, current.LockedUntil);
        }

        var next = Copy(current);
        if (current.LockedUntil is not null)
        {
            next.FailedLogins = 0;
            next.LockedUntil = null;
        }
        var ok = current.PasswordHash is not null && verification != PasswordVerification.Failed;
        if (ok)
        {
            next.FailedLogins = 0;
            if (verification == PasswordVerification.SuccessRehashNeeded)
            {
                next.PasswordHash = hasher.Hash(password);
            }
        }
        else if (++next.FailedLogins >= options.LoginLockoutThreshold)
        {
            next.LockedUntil = now + options.LoginLockoutDuration;
        }
        await SaveQuietly(next);
        return new PasswordCheck(ok, next.LockedUntil > now, next.LockedUntil);
    }

    public async Task RotatePassword(string passwordHash)
    {
        var next = Copy(Snapshot);
        next.PasswordHash = passwordHash;
        next.PasswordChangedAt = clock.GetUtcNow();
        next.FailedLogins = 0;
        next.LockedUntil = null;
        await SaveQuietly(next);
    }

    public Task<string?> GetEmailHash() => Task.FromResult(Snapshot.EmailHash);

    public async Task<bool> SetEmailHash(string emailHash)
    {
        // Provisioning initializes the binding once. Only the authenticated email-change CAS may move it.
        if (Snapshot.EmailHash is not null) { return Snapshot.EmailHash == emailHash; }
        var next = Copy(Snapshot);
        next.EmailHash = emailHash;
        await SaveQuietly(next);
        return true;
    }

    public async Task<bool> TryMoveEmailHash(string expectedHash, string nextHash)
    {
        if (Snapshot.EmailHash == nextHash) { return true; }
        if (Snapshot.EmailHash != expectedHash) { return false; }
        var next = Copy(Snapshot);
        next.EmailHash = nextHash;
        await SaveQuietly(next);
        return true;
    }

    public async Task<bool> BeginTotp(SealedSecret pending)
    {
        if (Snapshot.TotpSealed is not null) { return false; }
        var next = Copy(Snapshot);
        next.TotpPending = pending;
        await SaveQuietly(next);
        return true;
    }

    public async Task<string[]?> ConfirmTotp(string code)
    {
        if (Snapshot.TotpPending is not { } pending || Snapshot.TotpSealed is not null) { return null; }
        var result = new TotpVerifier(clock).Verify(await secrets.OpenTotp(PrincipalId, pending), code, 0);
        if (!result.Ok) { return null; }
        var next = Copy(Snapshot);
        next.TotpSealed = pending;
        next.TotpPending = null;
        next.TotpLastStep = result.MatchedStep;
        var (codes, hashes) = new RecoveryCodes(hashing.RootKey).Generate();
        next.RecoveryHashes = [.. hashes];
        await SaveQuietly(next);
        return [.. codes];
    }

    public async Task<bool> VerifyTotp(string code)
    {
        if (Snapshot.TotpSealed is not { } secret) { return false; }
        var result = new TotpVerifier(clock).Verify(await secrets.OpenTotp(PrincipalId, secret), code, Snapshot.TotpLastStep);
        if (!result.Ok) { return false; }
        var next = Copy(Snapshot);
        next.TotpLastStep = result.MatchedStep;
        await SaveQuietly(next);
        return true;
    }

    public async Task<bool> UseRecovery(string code)
    {
        if (Snapshot.TotpSealed is null) { return false; }
        var used = new RecoveryCodes(hashing.RootKey).Use(code, Snapshot.RecoveryHashes);
        if (!used.Matched) { return false; }
        var next = Copy(Snapshot);
        next.RecoveryHashes = [.. used.Remaining];
        await SaveQuietly(next);
        return true;
    }

    public async Task DisableTotp()
    {
        var next = Copy(Snapshot);
        next.TotpSealed = null;
        next.TotpPending = null;
        next.TotpLastStep = 0;
        next.RecoveryHashes = [];
        await SaveQuietly(next);
    }

    public async Task<string[]> RegenerateRecovery()
    {
        if (Snapshot.TotpSealed is null) { return []; }
        var (codes, hashes) = new RecoveryCodes(hashing.RootKey).Generate();
        var next = Copy(Snapshot);
        next.RecoveryHashes = [.. hashes];
        await SaveQuietly(next);
        return [.. codes];
    }
    public async Task SetPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        if (Snapshot.PasswordHash is not null)
        {
            // Sign-up completion is retried idempotently; the first stored hash wins.
            return;
        }

        var next = Copy(Snapshot);
        next.PasswordHash = passwordHash;
        next.PasswordChangedAt = clock.GetUtcNow();
        await SaveQuietly(next);
    }

    public Task<CredentialFlags> GetFlags() =>
        Task.FromResult(new CredentialFlags(Snapshot.PasswordHash is not null, Snapshot.TotpSealed is not null));

    private static AuthCredentialState Copy(AuthCredentialState state) => new()
    {
        PasswordHash = state.PasswordHash,
        FailedLogins = state.FailedLogins,
        LockedUntil = state.LockedUntil,
        TotpPending = state.TotpPending,
        TotpSealed = state.TotpSealed,
        TotpLastStep = state.TotpLastStep,
        RecoveryHashes = state.RecoveryHashes,
        PasswordChangedAt = state.PasswordChangedAt,
        EmailHash = state.EmailHash,
    };
}
