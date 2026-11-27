using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Flutter.Conversations;

public enum ThreadTrustPosture
{
    OwnerTrusted = 0,
    CrossBrainShared = 1,
}

public enum ThreadKind
{
    DirectAssistant = 0,
    Squad = 1,
    DirectPerson = 2,
    SharedRoom = 3,
}

public sealed record PinnedScreen(string ScreenType, string ScreenKey);

[NeuronAlias(IConversation.Type), DefaultNeuronType(IConversation.Type), Shareable, Offered, RoomSafe]
public interface IConversation
{
    const string Type = "conversation";

    static string KeyOf(ConversationId id) => "conversations/" + id.Value;

    Task Configure(ThreadTrustPosture posture, ThreadKind kind, string? title = null, string? leadAgentId = null);

    [Shared, Interleaves]
    Task Say(
        string text,
        ModelId? model = null,
        AttachmentRef[]? attachments = null,
        string? messageId = null,
        EntryId? replyTo = null);

    [Interleaves] Task Stop();

    [Interleaves] Task Regenerate(EntryId reply);

    [Shared, ReadOnlyCall, Interleaves] Task<ConversationView> Read(long? sinceRevision = null);

    Task Invite(AccountId friend, BrainId? friendBrain = null, Role role = Role.Editor);

    Task Pin(string screenType, string screenKey = "default");

    /// <summary>Sends a command through a pinned [Shareable] screen's Dispatch preserving the authenticated caller and returns its view after.</summary>
    [Shared]
    Task<PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command);

    [Shared, ReadOnlyCall]
    Task Here();

    [Shared]
    Task Leave();

    [Shared, Interleaves]
    Task React(EntryId target, string emoji, bool remove = false);

    [Shared, Interleaves]
    Task Edit(EntryId target, string text);

    [Shared, Interleaves]
    Task Delete(EntryId target);

    [Interleaves] Task Seat(string agentId, AgentDefinition definition);

    [Interleaves] Task Post(string text, string id, Visibility visibility = Visibility.Everyone);

    [Interleaves] Task Voiced(string heard, string reply, ModelId model, string id);
}
