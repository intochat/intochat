using DigitalBrain;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Apps;

public static class FriendRelay
{
    // One message for every refusal: it tells a caller nothing about which shares or groups exist.
    public static UnauthorizedAccessException Refused() => new("Sharing is refused for this caller.");

    // A cross-brain call from a person's relay neuron in their own brain: Inside is true, Via is that relay neuron in
    // caller.Brain, the actor is a person, and that person's account owns caller.Brain.
    public static async Task<(string Account, BrainId Brain)> Require(Caller? caller, string relayType, IBrainOwners owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (caller is not { Inside: true, Actor: Actor.Person person, Via: { } via }
            || string.IsNullOrEmpty(person.Account)
            || via.Brain != caller.Brain
            || !string.Equals(via.Type, relayType, StringComparison.Ordinal))
        {
            throw Refused();
        }

        var owner = await owners.OwnerAccount(caller.Brain).ConfigureAwait(true);
        return string.Equals(owner, person.Account, StringComparison.Ordinal) ? (person.Account, caller.Brain) : throw Refused();
    }
}
