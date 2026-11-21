using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Legacy registry kept for two releases after S3 so SessionListNeuron can lazily import old conversation order. See OWED.md.
public sealed record InoConversationsState(string[] Agents)
{
    public InoConversationsState() : this([]) { }
}

[NeuronAlias("ino.conversations"), DefaultNeuronType("ino.conversations")]
public interface IInoConversations
{
    const string DefaultKey = "ino/conversations";

    /// <summary>Returns all legacy tracked conversation agent keys (most recent first, up to 64).</summary>
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<string[]> List();
}
