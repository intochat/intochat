using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Identity;

internal sealed class BrainOwnerDispatch(IIdentityClient identity, EdgeAuthority edge) : IBrainOwnerDispatch
{
    public Task<bool> RunAsync(string brainId, string originKey, Func<Task> work, object? trace = null,
        CancellationToken cancellationToken = default)
        => RunAsync(brainId, originKey, work, trace as Trace, cancellationToken);

    public async Task<bool> RunAsync(string brainId, string originKey, Func<Task> work, Trace? trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(originKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (!BrainScope.IsValidId(brainId) || !BrainKey.BelongsTo(originKey, brainId))
        { throw new UnauthorizedAccessException("The work origin must belong to its target brain."); }
        if (await BrainOwners.VerifiedOwnerAsync(identity, brainId, cancellationToken) is not { } owner) { return false; }
        if (trace is not null && (trace.BrainId != brainId || trace.PayerAccountId != owner.AccountId))
        { throw new UnauthorizedAccessException("The resumed work does not belong to the persisted brain owner."); }
        cancellationToken.ThrowIfCancellationRequested();
        using var traceScope = TraceContext.Enter(trace ?? TraceContext.Begin(owner.AccountId, brainId, TraceContext.OriginOf(brainId, originKey))
            with
        { TraceId = Guid.NewGuid().ToString("N") });
        // Work resumed from storage runs as the scheduler of the brain it belongs to (owner decision 3, 17 Nov): no identity is
        // stored, so it never acts as the owner; anything that needs a person goes through confirmations.
        using var caller = edge.Enter(edge.Scheduler(new BrainId(brainId)));
        await work();
        return true;
    }
}
