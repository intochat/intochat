using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Agents.Observers;

// Watches one agent's finished turns and shapes the conversation.inference.tone context: three short user messages
// in a row ask for a concise persona. Keyed per agent, because an app cannot subscribe to a rotating agent.
[NeuronAlias("ai.tone-observer"), DefaultNeuronType("ai.tone-observer")]
public interface IToneObserver
{
    // agentKey is the agent's key, with or without the brain prefix.
    Task Follow(string agentKey);
}
