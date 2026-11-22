using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Units;

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

    [Shared]
    Task<PublishedView> Act(string screenType, string screenKey, ScreenAction action, CancellationToken ct = default);

    [Shared]
    Task Here(CancellationToken ct = default);
}

[Shareable, NeuronAlias("social.rooms"), DefaultNeuronType("social.rooms")]
public interface IRooms
{
    const string DefaultKey = "default";

    [Shared]
    Task Invited(NeuronId room, CancellationToken ct = default);

    [Shared]
    Task Changed(NeuronId room, long revision, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<long> LastRevision(NeuronId room, CancellationToken ct = default);
}
