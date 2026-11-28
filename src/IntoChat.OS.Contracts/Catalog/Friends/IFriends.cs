using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record Friend(
    AccountId Account,
    BrainId Brain,
    string Name,
    DateTimeOffset Since,
    BrainPublicKeys? PublicKeys = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RelayHint = null);

public sealed record GroupInvite(BrainId Brain, ConversationId Conversation, string Title, AccountId By);

// Facts of the roster. A neuron of the same brain that keeps something for a friend (a share, an offer, a seat in a room)
// reacts to them; the roster calls none of those neurons.
[DigitalBrain.Signal("friends.added")]
public sealed record FriendAdded(AccountId Account) : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.removed")]
public sealed record FriendRemoved(AccountId Account) : DigitalBrain.Signal;

public static class FriendsDefaults
{
    public const string TypeName = "friends.links";
    public const string KeyPart = "friends";

    public static NeuronId Id(string brain) => new(new BrainId(brain), TypeName, KeyPart);
}

// A brain's friend roster, at "{brain}/friends" (stored neuron type "friends.links"). A friend is a link both people
// confirmed with a single-use 24h code.
[Shareable, NeuronAlias("friends"), DefaultNeuronType(FriendsDefaults.TypeName)]
public interface IFriends
{
    Task<Code> Mint();
    Task<Friend> Redeem(Code code);
    Task Revoke(Code code);
    Task Unfriend(AccountId friend);
    Task<Friend[]> List();
    [Shared, Interleaves] Task<bool> IsFriend(AccountId account);
    [Interleaves, ReadOnlyCall] Task<AccountId?> AccountOf(BrainId brain);
    [Shared] Task<Friend> Accepted(string codeHash, string name, BrainPublicKeys? publicKeys = null, string? relayHint = null);
    [Shared] Task Unfriended();
    // The invitations to friends' conversations are kept by the rooms index (IRooms.Conversations, "social.rooms"); the
    // roster hands on what it is told and answers from there (OWED.md).
    Task<GroupInvite[]> Groups();
    [Shared] Task Invited(GroupInvite group);
    [ReadOnlyCall] Task<BrainPublicKeys> PublicKeys(CancellationToken ct = default);
}

/// <summary>
/// Non-reentrant-safe lookup of persisted remote peer metadata from a brain's <see cref="IFriends"/> roster.
/// </summary>
public interface IFriendPeerDirectory
{
    Task<AccountId?> AccountOf(BrainId owner, BrainId peerBrain, CancellationToken ct = default);
}
