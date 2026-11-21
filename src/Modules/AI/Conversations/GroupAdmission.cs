using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.AI.Conversations;

internal static class GroupAdmission
{
    public static async Task<AccountId> Admit(
        Caller? caller,
        BrainId conversationBrain,
        IReadOnlyList<Participant> participants,
        IFriends friends,
        IBrainOwners owners)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(friends);
        ArgumentNullException.ThrowIfNull(owners);

        var (account, callerBrain) = await FriendRelay.Require(caller, FriendsDefaults.TypeName, owners);
        if (callerBrain == conversationBrain)
        {
            throw FriendRelay.Refused();
        }

        var candidate = new AccountId(account);
        if (!participants.OfType<Participant.Human>().Any(p => p.Account == candidate))
        {
            throw FriendRelay.Refused();
        }

        if (!await friends.IsFriend(candidate))
        {
            throw FriendRelay.Refused();
        }

        return candidate;
    }
}
