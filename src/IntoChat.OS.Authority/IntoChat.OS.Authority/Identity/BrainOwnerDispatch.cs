using IntoChat.OS.Authority.Enforcement;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Sdk.Platform.Identity;

namespace IntoChat.OS.Authority.Identity;

internal sealed class BrainOwnerDispatch(IIdentityClient identity) : IBrainOwnerDispatch
{
    public async Task<bool> RunAsync(string brainId, NeuronId origin, Func<Task> work, Trace? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        if (!BrainScope.IsValidId(brainId) || !BrainKey.BelongsTo(origin.Name, brainId))
        { throw new UnauthorizedAccessException("The work origin must belong to its target brain."); }
        var owner = await identity.OwnerOfBrain(brainId).WaitAsync(cancellationToken);
        if (owner is null) { return false; }
        var member = await identity.Membership(brainId, owner.OwnerPrincipalId).WaitAsync(cancellationToken);
        if (member is null || member.AccountId != owner.AccountId || member.PrincipalId != owner.OwnerPrincipalId
            || member.BrainId != brainId || member.Role != MemberRole.Owner) { return false; }
        if (trace is not null && (trace.BrainId != brainId || trace.PayerAccountId != owner.AccountId))
        { throw new UnauthorizedAccessException("The resumed work does not belong to the persisted brain owner."); }
        cancellationToken.ThrowIfCancellationRequested();
        using var traceScope = TraceContext.Enter(trace ?? TraceContext.Begin(owner.AccountId, brainId, origin)
            with
        { TraceId = Guid.NewGuid().ToString("N") });
        using var caller = CallerContextScope.Scoped(AccountSession.ForMember(member));
        await work();
        return true;
    }
}
