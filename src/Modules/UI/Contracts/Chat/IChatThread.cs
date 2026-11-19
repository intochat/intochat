using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Chat;

[NeuronAlias("ui.chat")]
[DefaultNeuronType("ui.chat")]
public interface IChatThread
{
    Task<ChatThreadState> Configure(
        string conversationId,
        string title,
        string activeModel,
        IReadOnlyList<ChatModelOption> availableModels,
        bool voiceAvailable = false,
        long contextBudgetTokens = 128_000);

    Task<ChatThreadState> AppendMessage(ChatMessage message);

    /// <summary>Appends streaming text and/or reasoning tokens in place without full-thread replacement.</summary>
    Task<ChatThreadState> StreamDelta(string messageId, string? textDelta = null, string? reasoningDelta = null, string? status = null);

    /// <summary>Adds or updates an expandable DevUI tool-call step on a message.</summary>
    Task<ChatThreadState> UpsertToolCall(string messageId, ChatToolCall toolCall);

    /// <summary>Updates context provisioning inspector entries, compacted summary, and real token usage.</summary>
    Task<ChatThreadState> SetContext(
        IReadOnlyList<ChatContextItem> contextItems,
        string? contextSummary,
        long totalInputTokens,
        long totalOutputTokens);

    Task<ChatThreadState> SetWorking(bool working, string? error = null);

    /// <summary>User actions invoked directly from Flutter over gRPC NeuronChannel.</summary>
    Task<ChatThreadState> Submit(string text, IReadOnlyList<ChatAttachment>? attachments = null);
    Task<ChatThreadState> Stop();
    Task<ChatThreadState> Retry(string? messageId = null);
    Task<ChatThreadState> Compact();
    Task<ChatThreadState> SelectModel(string modelId);

    [ReadOnlyCall]
    Task<ChatThreadState> Read();
}
