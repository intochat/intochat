
namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neuron("auth.sessions")]
internal sealed class AuthSessionsNeuron(
    TimeProvider clock) : AuthNeuron<AuthSessionsState>, IAuthSessions
{
    // Revoked and expired rows are kept a day for the list, then pruned on the next write.
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    public async Task<SessionCreated> Create(string sidHash, string handle, string label, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        var now = clock.GetUtcNow();
        var row = new AuthSession
        {
            SidHash = sidHash,
            Handle = handle,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + lifetime,
            Label = string.IsNullOrWhiteSpace(label) ? "Other" : label,
        };
        await SaveQuietly(new AuthSessionsState { Sessions = [.. Pruned(now), row] });
        return new SessionCreated(row.Handle, row.ExpiresAt);
    }

    public async Task<SessionCreated> Issue(SessionOpen open)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentException.ThrowIfNullOrWhiteSpace(open.SidHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(open.Handle);
        var now = clock.GetUtcNow();
        var row = new AuthSession
        {
            SidHash = open.SidHash,
            Handle = open.Handle,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + open.Lifetime,
            Label = string.IsNullOrWhiteSpace(open.Label) ? "Other" : open.Label,
            PrincipalId = open.Member.PrincipalId,
            AccountId = open.Member.AccountId,
            BrainId = open.Member.BrainId,
            Role = open.Member.Role,
            DisplayName = open.Member.DisplayName,
        };
        await SaveQuietly(new AuthSessionsState { Sessions = [.. Pruned(now), row] });
        return new SessionCreated(row.Handle, row.ExpiresAt);
    }

    public Task<SessionMember?> Find(string sidHash)
    {
        var now = clock.GetUtcNow();
        return Task.FromResult(Snapshot.Sessions.FirstOrDefault(row => row.SidHash == sidHash) is { RevokedAt: null } row && row.ExpiresAt > now
            ? new SessionMember(row.PrincipalId, row.AccountId, row.BrainId, row.Role, row.DisplayName) : null);
    }

    public async Task Describe(string sidHash, string label)
    {
        if (string.IsNullOrWhiteSpace(label) || Array.Find(Snapshot.Sessions, row => row.SidHash == sidHash) is null) { return; }
        await SaveQuietly(new AuthSessionsState { Sessions = [.. Snapshot.Sessions.Select(row => row.SidHash == sidHash ? Relabelled(row, label) : row)] });
    }

    private static AuthSession Relabelled(AuthSession row, string label) => new()
    {
        SidHash = row.SidHash,
        Handle = row.Handle,
        CreatedAt = row.CreatedAt,
        LastSeenAt = row.LastSeenAt,
        ExpiresAt = row.ExpiresAt,
        Label = label,
        RevokedAt = row.RevokedAt,
        PrincipalId = row.PrincipalId,
        AccountId = row.AccountId,
        BrainId = row.BrainId,
        Role = row.Role,
        DisplayName = row.DisplayName,
    };

    public Task<bool> Check(string sidHash)
    {
        var now = clock.GetUtcNow();
        return Task.FromResult(Snapshot.Sessions.FirstOrDefault(row => row.SidHash == sidHash) is { RevokedAt: null } row && row.ExpiresAt > now);
    }

    public Task<SessionView[]> List(string? currentSidHash)
    {
        var now = clock.GetUtcNow();
        var current = currentSidHash;
        return Task.FromResult<SessionView[]>([.. Snapshot.Sessions
            .Where(row => row.RevokedAt is null && row.ExpiresAt > now)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => new SessionView(row.Handle, row.Label, row.CreatedAt, row.LastSeenAt, row.SidHash == current))]);
    }

    public async Task<bool> RevokeByHandle(string handle) => await Revoke(row => row.Handle == handle) > 0;

    public Task<int> RevokeOthers(string currentSidHash) => Revoke(row => row.SidHash != currentSidHash);

    public Task RevokeCurrent(string sidHash) => Revoke(row => row.SidHash == sidHash);

    public Task<int> RevokeAll() => Revoke(_ => true);

    private async Task<int> Revoke(Func<AuthSession, bool> which)
    {
        var now = clock.GetUtcNow();
        var rows = Pruned(now);
        var revoked = 0;
        var next = rows.Select(row =>
        {
            if (row.RevokedAt is not null || !which(row))
            {
                return row;
            }

            revoked++;
            return new AuthSession
            {
                SidHash = row.SidHash,
                Handle = row.Handle,
                CreatedAt = row.CreatedAt,
                LastSeenAt = row.LastSeenAt,
                ExpiresAt = row.ExpiresAt,
                Label = row.Label,
                RevokedAt = now,
                PrincipalId = row.PrincipalId,
                AccountId = row.AccountId,
                BrainId = row.BrainId,
                Role = row.Role,
                DisplayName = row.DisplayName,
            };
        }).ToArray();
        await SaveQuietly(new AuthSessionsState { Sessions = next });
        return revoked;
    }

    private AuthSession[] Pruned(DateTimeOffset now) =>
        [.. Snapshot.Sessions.Where(row => now - (row.RevokedAt ?? row.ExpiresAt) < Keep || (row.RevokedAt is null && row.ExpiresAt > now))];
}
