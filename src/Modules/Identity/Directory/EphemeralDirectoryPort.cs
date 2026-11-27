using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Identity.Directory;

/// <summary>
/// Thin OS host decorator over <see cref="LocalDirectoryPort"/> that:
/// 1. Serves RAM-only ephemeral guest brains from <see cref="EphemeralBrainRegistry"/> without touching durable storage.
/// 2. Enters the kernel caller for platform directory operations (leaving owner-gated operations under the stamped caller).
/// 3. Notifies <see cref="IBrainBirths"/> on registration completion.
/// </summary>
public sealed class EphemeralDirectoryPort : IIdentityDirectoryPort
{
    private readonly LocalDirectoryPort _inner;
    private readonly ICallerAmbient? _callers;
    private readonly EdgeAuthority? _edge;
    private readonly EphemeralBrainRegistry? _ephemeral;
    private readonly IBrainBirths? _births;
    private readonly IEdgeGrantsCache? _edgeCache;

    internal EphemeralDirectoryPort(
        LocalDirectoryPort inner,
        ICallerAmbient? callers = null,
        EdgeAuthority? edge = null,
        EphemeralBrainRegistry? ephemeral = null,
        IBrainBirths? births = null,
        IEdgeGrantsCache? edgeCache = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _callers = callers;
        _edge = edge;
        _ephemeral = ephemeral;
        _births = births;
        _edgeCache = edgeCache;
    }

    private async Task<T> AsPlatform<T>(Func<Task<T>> call)
    {
        if (_edge is null || (_callers?.Current ?? _edge.Current)?.Actor is Actor.Kernel)
        {
            return await call().ConfigureAwait(false);
        }

        using (_edge.Enter(_edge.Kernel(BrainId.Kernel)))
        {
            return await call().ConfigureAwait(false);
        }
    }

    private async Task AsPlatform(Func<Task> call)
    {
        if (_edge is null || (_callers?.Current ?? _edge.Current)?.Actor is Actor.Kernel)
        {
            await call().ConfigureAwait(false);
            return;
        }

        using (_edge.Enter(_edge.Kernel(BrainId.Kernel)))
        {
            await call().ConfigureAwait(false);
        }
    }

    public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.TryGet(accountId, out var rec)
            ? Task.FromResult<Account?>(rec.Account)
            : AsPlatform(() => _inner.ReadAccount(accountId, cancellationToken));

    public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.TryGet(brainId, out var rec)
            ? Task.FromResult<Account?>(rec.Account)
            : AsPlatform(() => _inner.OwnerOfBrain(brainId, cancellationToken));

    public Task EnsureAccount(Account account, CancellationToken cancellationToken)
        => AsPlatform(() => _inner.EnsureAccount(account, cancellationToken));

    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
        => AsPlatform(() => _inner.CreateBrain(accountId, principalId, operationId, displayName, kind, cancellationToken));

    public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
        => AsPlatform(() => _inner.EnsureBrain(owner, cancellationToken));

    public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
        => AsPlatform(() => _inner.ProvisionOwner(owner, cancellationToken));

    public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken)
        => AsPlatform(() => _inner.IsRegistered(principalId, cancellationToken));

    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
    {
        var provision = await AsPlatform(() => _inner.RegisterPrincipal(principalId, displayName, cancellationToken)).ConfigureAwait(false);
        if (_births is not null)
        {
            await _births.Born(provision).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return provision;
    }

    public async Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
    {
        var provision = await AsPlatform(() => _inner.CompleteRegistration(principalId, cancellationToken)).ConfigureAwait(false);
        if (provision is not null && _births is not null)
        {
            await _births.Born(provision).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return provision;
    }

    public Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.TryGet(principalId, out var rec)
            ? Task.FromResult<IReadOnlyList<BrainEntry>>([new BrainEntry(rec.BrainId, BrainKind.Personal)])
            : AsPlatform(() => _inner.BrainsOf(principalId, cancellationToken));

    public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.Contains(brainScopeId)
            ? Task.FromResult(BrainKind.Personal)
            : AsPlatform(() => _inner.KindOf(brainScopeId, cancellationToken));

    public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.Contains(brainScopeId)
            ? Task.FromResult(true)
            : AsPlatform(() => _inner.BrainExists(brainScopeId, cancellationToken));

    public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.TryGet(brainScopeId, out var rec)
            ? Task.FromResult<Member?>(string.Equals(rec.Owner.PrincipalId, principalId, StringComparison.Ordinal) ? rec.Owner : null)
            : AsPlatform(() => _inner.Membership(brainScopeId, principalId, cancellationToken));

    // Caller-decided owner operations: never enter kernel; preserve the stamped caller.
    public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        return _ephemeral is not null && _ephemeral.Contains(member.BrainId)
            ? Task.FromResult<Member?>(null)
            : _inner.EnsureMember(member, cancellationToken);
    }

    public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.Contains(brainScopeId)
            ? Task.CompletedTask
            : _inner.RemoveMember(brainScopeId, accountId, cancellationToken);

    public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.Contains(brainScopeId)
            ? Task.CompletedTask
            : _inner.RemovePrincipalMember(brainScopeId, principalId, cancellationToken);

    public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = _callers?.Current;
        if (caller is not null && _ephemeral is not null && _ephemeral.TryGetAuthorityRow(caller.Brain.Value, out var row))
        {
            return BrainAuthorityDecisions.Decide(row, caller, request, _edgeCache).WaitAsync(cancellationToken);
        }

        return _inner.Authorize(request, cancellationToken);
    }

    public Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
        => _ephemeral is not null && _ephemeral.TryGet(brainScopeId, out var rec)
            ? Task.FromResult<IReadOnlyList<Grant>>(rec.ToAuthorityRow().Grants)
            : AsPlatform(() => _inner.ListGrants(brainScopeId, cancellationToken));

    public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken)
        => _inner.GrantAsOwner(grant, cancellationToken);

    public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken)
        => _inner.RevokeAsOwner(appId, semanticTypeId, mode, cancellationToken);
}

public static class OsDirectoryHosting
{
    public static IServiceCollection AddOsDirectoryPort(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new LocalDirectoryPort(
            (contract, key) => SiloNeurons.Get(sp, contract, key),
            sp.GetService<TimeProvider>(),
            sp.GetService<ICallerAmbient>(),
            sp.GetService<EdgeAuthority>()));
        services.TryAddSingleton(sp => new EphemeralDirectoryPort(
            sp.GetRequiredService<LocalDirectoryPort>(),
            sp.GetService<ICallerAmbient>(),
            sp.GetService<EdgeAuthority>(),
            sp.GetService<EphemeralBrainRegistry>(),
            sp.GetService<IBrainBirths>(),
            sp.GetService<IEdgeGrantsCache>()));
        services.TryAddSingleton<IIdentityDirectoryPort>(sp => sp.GetRequiredService<EphemeralDirectoryPort>());
        return services;
    }
}
