using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// The user's Ino conversation agents, current first. Ino's set watch selects the current one, so it follows /new.
public sealed record InoConversationsState(string[] Agents)
{
    public InoConversationsState() : this([]) { }
}

[DigitalBrain.Signal("ino.conversations.changed")]
public sealed record InoConversationsChanged(string Current) : DigitalBrain.Signal;

[NeuronAlias("ino.conversations"), DefaultNeuronType("ino.conversations")]
public interface IInoConversations
{
    const string DefaultKey = "ino/conversations";

    // Makes agentKey the current conversation agent; emits InoConversationsChanged only when it changes.
    Task Use(string agentKey);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<string[]> Current();
}
