using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Agents.Observers;

// Watches one conversation's completed turns and shapes the conversation.inference.tone context: three short user
// messages in a row ask for a concise persona. Keyed per conversation.
[NeuronAlias("ai.tone-observer"), DefaultNeuronType("ai.tone-observer")]
public interface IToneObserver
{
    // conversationKey is the conversation's key, with or without the brain prefix.
    Task Follow(string conversationKey);
}
