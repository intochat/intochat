using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Circles;

// Acting in another brain is the stamped person acting there. The gate is a stamped person and a membership of that
// person's account in the target brain. The work then runs as that person in the target brain (through EdgeAuthority, keeping
// whether the person was relayed), so every call it makes is judged in that brain.
internal static class BrainActs
{
    public static async Task<T> In<T>(ICapabilityBroker broker, IServiceProvider plain, string brainId, Func<Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(work);
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException($"'{brainId}' is not a brain id.", nameof(brainId)); }
        var edge = plain.GetRequiredService<EdgeAuthority>();
        if (edge.Current is not { Actor: Actor.Person person } caller)
        { throw new UnauthorizedAccessException("Acting in another brain needs a stamped person."); }
        var directory = await broker.Identity();
        if (caller.Brain.Value != brainId
            && (await directory.Membership(brainId, person.Principal))?.AccountId != person.Account)
        { throw new UnauthorizedAccessException($"{person.Account} is not a member of brain {brainId}."); }
        var target = new BrainId(brainId);
        var there = caller.Direct
            ? edge.Person(person.Principal, person.Account, target, caller.Conversation)
            : edge.RelayedPerson(person.Principal, person.Account, target, caller.Via, caller.Conversation, inside: true);
        using (edge.Enter(there))
        {
            return await work();
        }
    }

    public static async Task In(ICapabilityBroker broker, IServiceProvider plain, string brainId, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await In(broker, plain, brainId, async () => { await work(); return true; });
    }
}
