using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Chat;

[NeuronAlias("ui.chat")]
[DefaultNeuronType("ui.chat")]
public interface IChatThread : IUiInteractiveControl
{
    Task<UiChatThreadState> Configure(
        string conversationId,
        string title,
        string activeModel,
        IReadOnlyList<ChatModelOption> availableModels,
        bool voiceAvailable = false,
        long contextBudgetTokens = 128_000);

    Task<UiChatThreadState> AppendMessage(ChatMessage message);

    /// <summary>Appends streaming text and/or reasoning tokens in place without full-thread replacement.</summary>
    Task<UiChatThreadState> StreamDelta(string messageId, string? textDelta = null, string? reasoningDelta = null, string? status = null);

    /// <summary>Adds or updates an expandable DevUI tool-call step on a message.</summary>
    Task<UiChatThreadState> UpsertToolCall(string messageId, ChatToolCall toolCall);

    /// <summary>Updates context provisioning inspector entries, compacted summary, and real token usage.</summary>
    Task<UiChatThreadState> SetContext(
        IReadOnlyList<ChatContextItem> contextItems,
        string? contextSummary,
        long totalInputTokens,
        long totalOutputTokens);

    Task<UiChatThreadState> SetWorking(bool working, string? error = null);

    /// <summary>User actions invoked directly from Flutter over gRPC NeuronChannel.</summary>
    Task<UiChatThreadState> Submit(string text, IReadOnlyList<ChatAttachment>? attachments = null);
    Task<UiChatThreadState> Stop();
    Task<UiChatThreadState> Retry(string? messageId = null);
    Task<UiChatThreadState> Compact();
    Task<UiChatThreadState> SelectModel(string modelId);

    [ReadOnlyCall]
    Task<UiChatThreadState> Read();
}
