using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Circles;

// Acting in another brain is the stamped person acting there. The gate is a trusted stamp and a membership of that
// person's account in the target brain. The work then runs re-stamped, so every call it makes is judged in that brain.
internal static class BrainActs
{
    public static async Task<T> In<T>(ICapabilityBroker broker, string brainId, Func<Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(work);
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException($"'{brainId}' is not a brain id.", nameof(brainId)); }
        if (!RuntimeCallerContext.TryGet(out var caller) || !RuntimeCallerContext.IsTrusted(caller))
        { throw new UnauthorizedAccessException("Acting in another brain needs a stamped caller."); }
        var directory = await broker.Identity();
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != brainId
            && (await directory.Membership(brainId, caller.PersonId))?.AccountId != caller.AccountId)
        { throw new UnauthorizedAccessException($"{caller.AccountId} is not a member of brain {brainId}."); }
        RuntimeCallerContext.Stamp(caller with { BrainId = brainId });
        try { return await work(); }
        finally { RuntimeCallerContext.Stamp(caller); }
    }

    public static async Task In(ICapabilityBroker broker, string brainId, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await In(broker, brainId, async () => { await work(); return true; });
    }
}
