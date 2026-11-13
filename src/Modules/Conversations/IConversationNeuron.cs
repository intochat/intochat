using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Conversations;

[NeuronAlias("conversations"), DefaultNeuronType("conversations")]
public interface IConversationNeuron
{
    [ReadOnlyCall]
    Task<ConversationSnapshot> Read();

    Task<ConversationTurn> AddTurn(string speaker, string content, ConversationTurnRole role = ConversationTurnRole.User, string? runId = null);

    Task<ActiveRun> StartRun(string? flowId = null, string? step = null, string? prompt = null);

    Task AdvanceRunStep(string runId, string step);

    Task CompleteRun(string runId, string? reply = null);

    Task FailRun(string runId, string error);

    Task CancelRun(string runId);

    Task<PendingApproval> RequestApproval(string approvalId, ConversationApprovalKind kind, string title, string detail, string? flowId = null, string? step = null);

    Task<PendingApproval> ResolveApproval(string approvalId, bool approved, string? resolver = null, string? reason = null);
}
