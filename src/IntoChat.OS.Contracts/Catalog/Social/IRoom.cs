using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

public sealed record RoomMessage(long Revision, string Author, string Text, DateTimeOffset At);

public sealed record PinnedScreen(string ScreenType, string ScreenKey);

public sealed record RoomView(
    long Revision,
    RoomMessage[] Messages,
    PinnedScreen[] PinnedScreens,
    string[] PresentAccounts);

[Shareable, Offered, NeuronAlias("social.room"), DefaultNeuronType("social.room")]
public interface IRoom
{
    Task Invite(string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default);

    Task Pin(string screenType, string screenKey = "default", CancellationToken ct = default);

    [Shared]
    Task Say(string text, CancellationToken ct = default);

    [Shared, ReadOnlyCall]
    Task<RoomView> Read(long? sinceRevision = null, CancellationToken ct = default);

    /// <summary>Sends a command through a pinned screen's Dispatch (a key path reaches its children through it) and returns its view after.</summary>
    [Shared]
    Task<PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command, CancellationToken ct = default);

    [Shared, ReadOnlyCall]
    Task Here(CancellationToken ct = default);
}

// A friend's conversation this person sits in as a guest: where it is, what it is called and who invited them.
public sealed record ConversationInvite(BrainId Brain, ConversationId Conversation, string Title, AccountId By);

// A person's index of the places they were invited to: rooms, and friends' conversations.
[Shareable, NeuronAlias("social.rooms"), DefaultNeuronType("social.rooms")]
public interface IRooms
{
    const string DefaultKey = "default";

    // A friend seated this person in a conversation. Taken from that conversation, in the brain of a friend who is the
    // inviter, or from this brain's own roster handing on an invitation it took that way. Added after the first version
    // of this contract: a brain that runs the first version does not serve it, so nothing sends it to another brain yet
    // (OWED.md).
    [Shared]
    Task InvitedToConversation(ConversationInvite invite, CancellationToken ct = default);

    // This brain's roster hands over the invitations it kept before they moved here. One the index holds is left as it is.
    Task AdoptConversations(ConversationInvite[] invites, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<ConversationInvite[]> Conversations(CancellationToken ct = default);

    [Shared]
    Task Invited(NeuronId room, CancellationToken ct = default);

    [Shared]
    Task Changed(NeuronId room, long revision, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<long> LastRevision(NeuronId room, CancellationToken ct = default);
}
