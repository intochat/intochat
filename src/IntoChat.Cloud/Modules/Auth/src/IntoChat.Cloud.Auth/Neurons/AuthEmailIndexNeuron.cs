
namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neuron("auth.email-index")]
internal sealed class AuthEmailIndexNeuron(
    TimeProvider clock)
    : AuthNeuron<AuthEmailIndexState>, IAuthEmailIndex
{
    public async Task<AuthEmailIndexState> Get()
    {
        if (Snapshot.Status == EmailIndexStatus.ChangePending && !Snapshot.ChangeProved && Snapshot.ChangeExpiresAt <= clock.GetUtcNow())
        {
            await SaveQuietly(new AuthEmailIndexState());
        }
        return Copy(Snapshot);
    }

    public async Task<string?> SetPending(PendingSignUp pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var current = Snapshot.Status == EmailIndexStatus.ChangePending && !Snapshot.ChangeProved && Snapshot.ChangeExpiresAt <= clock.GetUtcNow()
            ? new AuthEmailIndexState() : Snapshot;
        if (current.Status is EmailIndexStatus.Verified or EmailIndexStatus.ChangePending)
        {
            return null;
        }

        var next = Copy(current);
        next.Status = EmailIndexStatus.Pending;
        // A principal id, once chosen for this address, survives re-sign-ups so a half-finished registration resumes.
        next.PrincipalId ??= "u-" + Guid.NewGuid().ToString("N");
        next.Email = pending.Email;
        next.PendingPasswordHash = pending.PasswordHash;
        next.PendingDisplayName = pending.DisplayName;
        next.PendingLanguage = pending.Language;
        next.ConsentAt = pending.ConsentAt;
        await SaveQuietly(next);
        return next.PrincipalId;
    }

    public async Task<bool> MarkVerified(string principalId, string accountId, string brainId)
    {
        var current = Snapshot;
        if (current.Status == EmailIndexStatus.Verified)
        {
            return current.PrincipalId == principalId;
        }
        if (current.Status != EmailIndexStatus.Pending || current.PrincipalId != principalId) { return false; }

        var next = Copy(current);
        next.Status = EmailIndexStatus.Verified;
        next.PrincipalId = principalId;
        next.AccountId = accountId;
        next.BrainId = brainId;
        next.PendingPasswordHash = null;
        await SaveQuietly(next);
        return true;
    }

    public async Task ClearPending()
    {
        if (Snapshot.Status != EmailIndexStatus.Pending)
        {
            return;
        }

        await SaveQuietly(new AuthEmailIndexState());
    }

    public async Task<bool> ReserveForChange(string principalId, string accountId, string brainId, string email, string previousEmailHash)
    {
        var current = Snapshot;
        if (current.Status == EmailIndexStatus.Verified || current.ChangeProved
            || (current.Status == EmailIndexStatus.ChangePending && current.ChangeExpiresAt > clock.GetUtcNow() && current.PrincipalId != principalId))
        {
            return false;
        }
        await SaveQuietly(new AuthEmailIndexState
        {
            Status = EmailIndexStatus.ChangePending, PrincipalId = principalId, AccountId = accountId, BrainId = brainId,
            Email = email, PreviousEmailHash = previousEmailHash, ChangeExpiresAt = clock.GetUtcNow() + TimeSpan.FromMinutes(10),
        });
        return true;
    }

    public async Task<bool> CommitChange(string principalId, string codeId)
    {
        var next = Copy(Snapshot);
        if (next.PrincipalId != principalId)
        {
            return false;
        }
        if (next.Status == EmailIndexStatus.Verified)
        {
            return next.ChangeCodeId == codeId;
        }
        if (next.Status != EmailIndexStatus.ChangePending || !next.ChangeProved || next.ChangeCodeId != codeId)
        {
            return false;
        }
        next.Status = EmailIndexStatus.Verified;
        next.ChangeCodeId = codeId;
        await SaveQuietly(next);
        return true;
    }

    public async Task<bool> BindChangeCode(string principalId, string codeId)
    {
        var next = Copy(Snapshot);
        if (next.Status != EmailIndexStatus.ChangePending || next.PrincipalId != principalId || next.ChangeProved || next.ChangeExpiresAt <= clock.GetUtcNow()) { return false; }
        next.ChangeCodeId = codeId;
        await SaveQuietly(next);
        return true;
    }

    public async Task<bool> ProveChange(string principalId, string codeId)
    {
        var next = Copy(Snapshot);
        if (next.Status != EmailIndexStatus.ChangePending || next.PrincipalId != principalId || next.ChangeCodeId != codeId) { return false; }
        if (next.ChangeProved) { return true; }
        if (next.ChangeExpiresAt <= clock.GetUtcNow()) { return false; }
        // Once proved, this is a durable in-flight index move. Expiry may not discard it after the credential CAS.
        next.ChangeProved = true;
        await SaveQuietly(next);
        return true;
    }

    public async Task<bool> RebindChangeCode(string principalId, string previousCodeId, string codeId)
    {
        var next = Copy(Snapshot);
        if (next.Status != EmailIndexStatus.ChangePending || next.PrincipalId != principalId || next.ChangeCodeId != previousCodeId
            || next.ChangeProved || next.ChangeExpiresAt <= clock.GetUtcNow()) { return false; }
        next.ChangeCodeId = codeId;
        await SaveQuietly(next);
        return true;
    }

    public Task CancelChange(string principalId, string codeId) =>
        Snapshot.Status == EmailIndexStatus.ChangePending && Snapshot.PrincipalId == principalId && Snapshot.ChangeCodeId == codeId
            ? SaveQuietly(new AuthEmailIndexState()) : Task.CompletedTask;

    public Task Release(string principalId) => Snapshot.PrincipalId == principalId
        ? SaveQuietly(new AuthEmailIndexState()) : Task.CompletedTask;

    public async Task<AuthEmailIndexState?> PrepareExternal(string email, string displayName, string language)
    {
        var next = Copy(Snapshot);
        if (next.Status == EmailIndexStatus.ChangePending)
        {
            if (next.ChangeProved || next.ChangeExpiresAt > clock.GetUtcNow()) { return null; }
            // A reservation names an existing principal from another email. Expiry releases the destination;
            // its verified owner must never inherit the reserving principal's identity or account.
            next = new AuthEmailIndexState();
        }
        if (next.Status == EmailIndexStatus.Verified) { return next; }
        next.Status = EmailIndexStatus.Pending;
        next.PrincipalId ??= "u-" + Guid.NewGuid().ToString("N");
        next.Email = email;
        // A verified external provider must never adopt a password placed by an unverified sign-up.
        next.PendingPasswordHash = null;
        next.PendingDisplayName = displayName;
        next.PendingLanguage = language;
        await SaveQuietly(next);
        return Copy(next);
    }

    public async Task<bool> LinkGoogle(string principalId, string sub)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sub);
        var next = Copy(Snapshot);
        if (next.PrincipalId != principalId || (next.GoogleSub is not null && next.GoogleSub != sub)) { return false; }
        next.GoogleSub = sub;
        await SaveQuietly(next);
        return true;
    }

    private static AuthEmailIndexState Copy(AuthEmailIndexState state) => new()
    {
        Status = state.Status,
        PrincipalId = state.PrincipalId,
        AccountId = state.AccountId,
        BrainId = state.BrainId,
        PendingPasswordHash = state.PendingPasswordHash,
        PendingDisplayName = state.PendingDisplayName,
        PendingLanguage = state.PendingLanguage,
        ConsentAt = state.ConsentAt,
        GoogleSub = state.GoogleSub,
        Email = state.Email,
        ChangeExpiresAt = state.ChangeExpiresAt,
        PreviousEmailHash = state.PreviousEmailHash,
        ChangeCodeId = state.ChangeCodeId,
        ChangeProved = state.ChangeProved,
    };
}
