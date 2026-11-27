using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

public enum RoomKind
{
    Direct,
    Group,
}

[Signal("social.rooms.index-changed")]
public sealed record RoomsIndexChanged(BrainId Brain, NeuronId Room, string Action) : Signal;

[Signal("social.rooms.room-changed")]
public sealed record RoomChanged(NeuronId Room, long Revision) : Signal;

public sealed record RoomMessage(
    long Revision,
    string Author,
    string Text,
    DateTimeOffset At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SenderIdentity? Sender = null);

public sealed record PinnedScreen(string ScreenType, string ScreenKey);

public sealed record RoomView(
    long Revision,
    RoomMessage[] Messages,
    PinnedScreen[] PinnedScreens,
    string[] PresentAccounts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoomKind? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Participants = null);

public sealed record RoomSummary(
    NeuronId Room,
    string Title,
    RoomKind Kind,
    long LastRevision,
    DateTimeOffset UpdatedAt,
    bool IsHost);

[Shareable, Offered, NeuronAlias("social.room"), DefaultNeuronType("social.room")]
public interface IRoom
{
    Task Configure(string title, RoomKind kind, CancellationToken ct = default);

    Task Invite(string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default);

    Task Pin(string screenType, string screenKey = "default", CancellationToken ct = default);

    [Shared]
    Task Say(string text, CancellationToken ct = default);

    [Shared, ReadOnlyCall]
    Task<RoomView> Read(long? sinceRevision = null, CancellationToken ct = default);

    /// <summary>Sends a command through a pinned [Shareable] screen's Dispatch (a key path reaches its children through it) and returns its view after.</summary>
    [Shared]
    Task<PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command, CancellationToken ct = default);

    [Shared, ReadOnlyCall]
    Task Here(CancellationToken ct = default);

    [Shared]
    Task Leave(CancellationToken ct = default);
}

[Internal, NeuronAlias("room-driver"), DefaultNeuronType("social.room")]
public interface IRoomDriver
{
    [OneWayCall, Interleaves]
    Task RespondAsRoomIno(long triggerRevision);
}

// A friend's conversation this person sits in as a guest: where it is, what it is called and who invited them.
public sealed record ConversationInvite(BrainId Brain, ConversationId Conversation, string Title, AccountId By);

// A person's index of the rooms they own or were invited to.
[Shareable, NeuronAlias("social.rooms"), DefaultNeuronType("social.rooms")]
public interface IRooms
{
    const string DefaultKey = "default";

    [ReadOnlyCall]
    Task<RoomSummary[]> List(CancellationToken ct = default);

    Task<NeuronId> Create(string title, RoomKind kind = RoomKind.Group, string? roomKey = null, CancellationToken ct = default);

    Task Invite(NeuronId room, string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default);

    Task Leave(NeuronId room, CancellationToken ct = default);

    [Shared]
    Task InvitedToConversation(ConversationInvite invite, CancellationToken ct = default);

    Task AdoptConversations(ConversationInvite[] invites, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<ConversationInvite[]> Conversations(CancellationToken ct = default);

    [Shared]
    Task Invited(NeuronId room, CancellationToken ct = default, string? title = null, RoomKind? kind = null);

    [Shared]
    Task Changed(NeuronId room, long revision, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<long> LastRevision(NeuronId room, CancellationToken ct = default);
}
