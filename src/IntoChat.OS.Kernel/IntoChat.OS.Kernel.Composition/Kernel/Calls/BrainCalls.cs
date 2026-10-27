using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Kernel.Composition;

// IBrainCalls (06-08, R4-21(3)): acting in another brain is the stamped person acting there. The gate is checked before any call
// leaves: a trusted stamp, and a membership of that person's account in the target brain (the identity record the brain routes
// check too). The work then runs re-stamped, so every call it makes reaches its target as a call made in that brain, through the
// target's access policy (PlainNeuronCallFilter) and its own checks; RuntimeCallerMirror carries the same stamp to the runtime.
internal sealed class BrainCalls(IIdentityClient identity) : IBrainCalls
{
    public async Task<T> In<T>(string brainId, Func<Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(work);
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException($"'{brainId}' is not a brain id.", nameof(brainId)); }
        if (!CallerContextStamper.TryGet(out var caller) || !CallerContextStamper.IsTrusted(caller))
        { throw new UnauthorizedAccessException("Acting in another brain needs a stamped caller."); }
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != brainId
            && (await identity.Membership(brainId, caller.PersonId))?.AccountId != caller.AccountId)
        { throw new UnauthorizedAccessException($"{caller.AccountId} is not a member of brain {brainId}."); }
        CallerContextStamper.Stamp(caller with { BrainId = brainId });
        try { return await work(); }
        finally { CallerContextStamper.Stamp(caller); }
    }
}
