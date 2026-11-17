namespace DigitalBrain.Identity.Local.Directory;

using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Identity.Model;

/// <summary>
/// Implements <see cref="IIdentityDirectoryPort"/> and <see cref="IIdentitySessionPort"/> over <see cref="DirectoryNeuron"/> and
/// <see cref="SessionsNeuron"/>. The members that decide by the caller reach the directory under the caller the runtime
/// stamped; the platform's members run only under a kernel caller the host stamped (read from <paramref name="callers"/>, else <paramref name="edge"/>); otherwise they are refused.
/// </summary>
public sealed class LocalDirectoryPort(
    Func<Type, string, object> neuronResolver,
    TimeProvider? time = null,
    ICallerAmbient? callers = null,
    EdgeAuthority? edge = null)
    : IIdentityDirectoryPort, IIdentitySessionPort
{
    private static readonly IdentityProviderDescriptor LocalDescriptor = new()
    {
        ProviderId = "local",
        Posture = IdentityPosture.Open,
    };

    private static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(24);

    private readonly Func<Type, string, object> _neuronResolver = neuronResolver ?? throw new ArgumentNullException(nameof(neuronResolver));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().ReadAccount(accountId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().OwnerOfBrain(brainId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task EnsureAccount(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var stamped = account.CreatedAt == default ? account with { CreatedAt = _time.GetUtcNow() } : account;
        return AsKernel(() => Directory().EnsureAccount(stamped)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<BrainProvision> CreateBrain(
        string accountId,
        string principalId,
        string operationId,
        string displayName,
        BrainKind kind,
        CancellationToken cancellationToken)
        => AsKernel(() => Directory().CreateBrain(accountId, principalId, operationId, displayName, kind)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return AsKernel(() => Directory().EnsureBrain(owner)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return AsKernel(() => Directory().ProvisionOwner(owner)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().IsRegistered(principalId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        return AsKernel(() => Directory().RegisterPrincipal(principalId, displayName)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().CompleteRegistration(principalId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
        => await AsKernel(() => Directory().BrainsOf(principalId)).WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().KindOf(brainScopeId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().BrainExists(brainScopeId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => AsKernel(() => Directory().Membership(brainScopeId, principalId)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        return Directory().EnsureMember(member).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken)
        => Directory().RemoveMember(brainScopeId, accountId).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => Directory().RemovePrincipalMember(brainScopeId, principalId).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Directory().Authorize(request).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
        => await AsKernel(() => Directory().ListGrants(brainScopeId)).WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var brainId = string.IsNullOrWhiteSpace(grant.WorkspaceId) ? Stamped().Brain.Value : grant.WorkspaceId;
        return Directory().GrantAsOwner(brainId, grant).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RevokeAsOwner(
        string appId,
        string semanticTypeId,
        GrantMode mode,
        CancellationToken cancellationToken)
        => Directory().RevokeAsOwner(Stamped().Brain.Value, appId, semanticTypeId, mode).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken)
    {
        SessionClaims.Validate(request);
        var member = request.Member;
        var current = await AsKernel(() => Directory().Membership(member.BrainId, member.PrincipalId)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || current.AccountId != member.AccountId)
        {
            throw new InvalidOperationException($"{member.PrincipalId} is not a member of {member.BrainId}; no session is issued.");
        }

        var sessionId = $"session-{Guid.NewGuid():N}";
        var expiresAt = _time.GetUtcNow() + (request.Lifetime ?? DefaultSessionLifetime);
        var tokenHash = SessionBook.HashToken(sessionId);
        await AsKernel(() => Sessions().Start(tokenHash, member.BrainId, member.PrincipalId, expiresAt)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionIssued(sessionId, expiresAt);
    }

    /// <inheritdoc />
    public Task End(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var tokenHash = SessionBook.HashToken(sessionId);
        return AsKernel(() => Sessions().End(tokenHash)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Member?> Validate(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var tokenHash = SessionBook.HashToken(sessionId);
        var session = await AsKernel(() => Sessions().Find(tokenHash)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        return await AsKernel(() => Directory().Membership(session.BrainId, session.PrincipalId)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IdentityProviderDescriptor Describe() => LocalDescriptor;

    private Caller Stamped()
        => callers?.Current ?? throw new UnauthorizedAccessException("identity@1: the call carries no caller.");

    // The platform's members and the session store run only when the host already stamped the kernel; the port never enters
    // that authority itself (a host wraps it, as PlatformIdentityDirectoryPort does on Orleans).
    private void RequireKernel()
    {
        if ((callers?.Current ?? edge?.Current)?.Actor is not Actor.Kernel)
        {
            throw new UnauthorizedAccessException("identity@1: only the platform (the kernel) may call this directory member.");
        }
    }

    private Task<T> AsKernel<T>(Func<Task<T>> call)
    {
        RequireKernel();
        return call();
    }

    private Task AsKernel(Func<Task> call)
    {
        RequireKernel();
        return call();
    }

    private IDirectory Directory()
        => (IDirectory)_neuronResolver(typeof(IDirectory), IDirectory.Key);

    private ISessionStore Sessions()
        => (ISessionStore)_neuronResolver(typeof(ISessionStore), ISessionStore.Key);
}
