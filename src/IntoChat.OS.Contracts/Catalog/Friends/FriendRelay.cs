using DigitalBrain;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Apps;

public static class FriendRelay
{
    // One message for every refusal: it tells a caller nothing about which shares or groups exist.
    public static UnauthorizedAccessException Refused() => new("Sharing is refused for this caller.");

    // A cross-brain call from a person's relay neuron in their own brain: Inside is true, Via is that relay neuron in
    // caller.Brain, the actor is a person, and that person's account owns caller.Brain (either via local IBrainOwners,
    // persisted friendship metadata for a remote brain, or a cryptographically verified remote handshake).
    public static Task<(string Account, BrainId Brain)> Require(Caller? caller, string relayType, IBrainOwners owners)
        => Require(caller, relayType, owners, resolvePeerAccount: null, allowVerifiedRemoteHandshake: false);

    public static async Task<(string Account, BrainId Brain)> Require(
        Caller? caller,
        string relayType,
        IBrainOwners owners,
        Func<BrainId, CancellationToken, ValueTask<string?>>? resolvePeerAccount,
        bool allowVerifiedRemoteHandshake = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (caller is not { Inside: true, Actor: Actor.Person person, Via: { } via }
            || string.IsNullOrEmpty(person.Account)
            || via.Brain != caller.Brain
            || !string.Equals(via.Type, relayType, StringComparison.Ordinal))
        {
            throw Refused();
        }

        var owner = await owners.OwnerAccount(caller.Brain, ct).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(owner))
        {
            return string.Equals(owner, person.Account, StringComparison.Ordinal) ? (person.Account, caller.Brain) : throw Refused();
        }

        if (resolvePeerAccount is not null)
        {
            var peerAccount = await resolvePeerAccount(caller.Brain, ct).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(peerAccount))
            {
                return string.Equals(peerAccount, person.Account, StringComparison.Ordinal) ? (person.Account, caller.Brain) : throw Refused();
            }
        }

        if (allowVerifiedRemoteHandshake && caller.RemoteVerified)
        {
            return (person.Account, caller.Brain);
        }

        throw Refused();
    }
}
