using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Flutter.Conversations;

[NeuronAlias(IConversation.Type), DefaultNeuronType(IConversation.Type), Shareable]
public interface IConversation
{
    const string Type = "conversation";

    static string KeyOf(ConversationId id) => "conversations/" + id.Value;

    [Shared, Interleaves] Task Say(string text, ModelId? model = null);

    [Interleaves] Task Stop();

    [Interleaves] Task Regenerate(EntryId reply);

    [Shared, ReadOnlyCall, Interleaves] Task<ConversationView> Read(long? sinceRevision = null);

    Task Invite(AccountId friend);

    [Interleaves] Task Seat(string agentId, AgentDefinition definition);

    [Interleaves] Task Post(string text, string id);

    [Interleaves] Task Voiced(string heard, string reply, ModelId model, string id);
}
