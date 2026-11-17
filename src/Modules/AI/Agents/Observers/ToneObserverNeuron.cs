using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.AI.Agents.Observers;

// One observer per agent (ai.tone-observer, key observer/tone/{agent key}). After each completed turn the last three
// user messages decide: all under ShortLimit characters sets the concise persona, anything else clears it.
[Neuron("ai.tone-observer")]
internal sealed class ToneObserverNeuron : StatelessAiNeuron, IToneObserver
{
    private const int ShortLimit = 40, Streak = 3;
    private const string Persona = """{"persona":"concise"}""", EntryName = "tone", KeyPrefix = "observer/tone/";

    private IAgentContext Tone => Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", "context/conversation.inference.tone"));

    // The agent this observer watches, by the key it was created with.
    private NeuronId AgentId => new(Id.Brain, AgentSignals.NeuronType, Id.Key[KeyPrefix.Length..]);

    public async Task Follow(string agentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        var prefix = Id.Brain.Value + "/";
        var relative = agentKey.StartsWith(prefix, StringComparison.Ordinal) ? agentKey[prefix.Length..] : agentKey;
        if (!string.Equals(relative, AgentId.Key, StringComparison.Ordinal)) { throw new ArgumentException("This observer watches another agent.", nameof(agentKey)); }
        await Context.Wires.Ensure<TurnCompleted>(AgentId);
    }

    [On]
    public async Task OnTurnCompleted(TurnCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var transcript = await Get<IAgent>(AgentId).Transcript();
        var users = transcript.Where(message => message.Role == "user")
            .Select(message => string.Concat(message.Content.OfType<AiText>().Select(part => part.Text))).TakeLast(Streak).ToArray();
        if (users.Length == Streak && users.All(text => text.Length < ShortLimit)) { await Tone.Set(new ContextEntry(EntryName, Persona, [], [ContextTags.Inference])); }
        else { await Tone.Remove(EntryName); }
    }
}
