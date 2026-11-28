using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Conversations;

[NeuronAlias("conversation-driver")]
internal interface IConversationDriver
{
    [OneWayCall, Interleaves]
    Task Drive(string runId, string? model, string? replaces, string? targetAgentId = null, int depth = 0, string[]? queuedAgentIds = null);

    [OneWayCall, Interleaves]
    Task DriveSharedRoomAgent(long triggerRevision, string agentId);
}
