using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Access;

namespace DigitalBrain.Runtime.Orleans.Capabilities;

/// <summary>
/// Wraps an <see cref="IIdentityDirectoryPort"/> so the platform's directory members run as the kernel. The members that decide
/// by the caller pass through under the caller the runtime stamped.
/// </summary>
internal sealed class PlatformIdentityDirectoryPort(IIdentityDirectoryPort inner) : IIdentityDirectoryPort
{
    public IIdentityDirectoryPort Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.ReadAccount(accountId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.OwnerOfBrain(brainId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnsureAccount(Account account, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            await Inner.EnsureAccount(account, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.CreateBrain(accountId, principalId, operationId, displayName, kind, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.EnsureBrain(owner, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.ProvisionOwner(owner, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.IsRegistered(principalId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.RegisterPrincipal(principalId, displayName, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.CompleteRegistration(principalId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DigitalBrain.Capabilities.Identity.BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.BrainsOf(principalId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.KindOf(brainScopeId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.BrainExists(brainScopeId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.Membership(brainScopeId, principalId, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken)
        => Inner.EnsureMember(member, cancellationToken);

    public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken)
        => Inner.RemoveMember(brainScopeId, accountId, cancellationToken);

    public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => Inner.RemovePrincipalMember(brainScopeId, principalId, cancellationToken);

    public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken)
        => Inner.Authorize(request, cancellationToken);

    public async Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.ListGrants(brainScopeId, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken)
        => Inner.GrantAsOwner(grant, cancellationToken);

    public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken)
        => Inner.RevokeAsOwner(appId, semanticTypeId, mode, cancellationToken);
}

/// <summary>
/// Wraps an <see cref="IIdentitySessionPort"/> so session store invocations run as the kernel; the privileged check already ran
/// against the stamped caller.
/// </summary>
internal sealed class PlatformIdentitySessionPort(IIdentitySessionPort inner) : IIdentitySessionPort
{
    public IIdentitySessionPort Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.Issue(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task End(string sessionId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            await Inner.End(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Member?> Validate(string sessionId, CancellationToken cancellationToken)
    {
        using (PlatformScope.Kernel())
        {
            return await Inner.Validate(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public IdentityProviderDescriptor Describe() => Inner.Describe();
}

file static class PlatformScope
{
    // The kernel of the system brain: the platform reaching its own identity store.
    internal static IDisposable Kernel() => OrleansCallerAmbient.Instance.Enter(Caller.OfKernel(BrainId.Kernel));
}
