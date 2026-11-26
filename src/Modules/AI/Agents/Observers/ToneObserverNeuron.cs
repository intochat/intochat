using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Agents.Observers;

// One observer per conversation (ai.tone-observer, key observer/tone/{conversation key}). After each completed turn
// the last three user messages decide: all under ShortLimit characters sets the concise persona, anything else clears it.
[Neuron("ai.tone-observer")]
internal sealed class ToneObserverNeuron : StatelessAiNeuron, IToneObserver
{
    private const int ShortLimit = 40, Streak = 3;
    private const string Persona = """{"persona":"concise"}""", EntryName = "tone", KeyPrefix = "observer/tone/";

    private IAgentContext Tone => Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", "context/conversation.inference.tone"));

    // The conversation this observer watches, by the key it was created with.
    private NeuronId ConversationNeuronId => new(Id.Brain, IConversation.Type, Id.Key[KeyPrefix.Length..]);

    public async Task Follow(string conversationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        var prefix = Id.Brain.Value + "/";
        var relative = conversationKey.StartsWith(prefix, StringComparison.Ordinal) ? conversationKey[prefix.Length..] : conversationKey;
        if (!string.Equals(relative, ConversationNeuronId.Key, StringComparison.Ordinal)) { throw new ArgumentException("This observer watches another conversation.", nameof(conversationKey)); }
        await Context.Wires.Ensure<ConversationChanged>(ConversationNeuronId);
    }

    [On]
    public async Task OnConversationChanged(ConversationChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var view = await Get<IConversation>(ConversationNeuronId).Read();
        if (view.Active is not null) { return; }
        var users = view.Entries
            .OfType<Said>()
            .Where(static entry => entry.By is Participant.Human)
            .Select(static entry => entry.Text)
            .TakeLast(Streak)
            .ToArray();
        if (users.Length == Streak && users.All(static text => text.Length < ShortLimit)) { await Tone.Set(new ContextEntry(EntryName, Persona, [], [ContextTags.Inference])); }
        else { await Tone.Remove(EntryName); }
    }
}
