using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Chat.Signals;

namespace IntoChat.OS.Modules.UI.Chat;

[Neuron("ui.chat")]
internal sealed class ChatThreadNeuron : Neuron<UiChatThreadState>, IChatThread
{
    public async Task<UiChatThreadState> Configure(
        string conversationId,
        string title,
        string activeModel,
        IReadOnlyList<ChatModelOption> availableModels,
        bool voiceAvailable = false,
        long contextBudgetTokens = 128_000)
    {
        var next = State with
        {
            Version = State.Version + 1,
            ConversationId = string.IsNullOrWhiteSpace(conversationId) ? State.ConversationId : conversationId.Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? State.Title : title.Trim(),
            ActiveModel = activeModel ?? State.ActiveModel,
            AvailableModels = availableModels is not null ? [.. availableModels] : State.AvailableModels,
            VoiceAvailable = voiceAvailable,
            ContextBudgetTokens = Math.Max(1_000, contextBudgetTokens),
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<UiChatThreadState> AppendMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Id);
        var list = State.Messages.ToList();
        var existing = list.FindIndex(m => string.Equals(m.Id, message.Id, StringComparison.Ordinal));
        if (existing >= 0)
        {
            list[existing] = message;
        }
        else
        {
            list.Add(message);
            if (list.Count > UiChatThreadState.MaxMessages)
            {
                list.RemoveRange(0, list.Count - UiChatThreadState.MaxMessages);
            }
        }
        var next = State with { Version = State.Version + 1, Messages = [.. list] };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<UiChatThreadState> StreamDelta(string messageId, string? textDelta = null, string? reasoningDelta = null, string? status = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var list = State.Messages.ToList();
        var idx = list.FindIndex(m => string.Equals(m.Id, messageId, StringComparison.Ordinal));
        if (idx < 0)
        {
            list.Add(new ChatMessage(messageId, "assistant", textDelta ?? "", reasoningDelta ?? "", Status: status ?? "streaming"));
        }
        else
        {
            var cur = list[idx];
            list[idx] = cur with
            {
                Text = string.IsNullOrEmpty(textDelta) ? cur.Text : cur.Text + textDelta,
                Reasoning = string.IsNullOrEmpty(reasoningDelta) ? cur.Reasoning : cur.Reasoning + reasoningDelta,
                Status = string.IsNullOrWhiteSpace(status) ? cur.Status : status,
            };
        }
        var next = State with { Version = State.Version + 1, Messages = [.. list] };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<UiChatThreadState> UpsertToolCall(string messageId, ChatToolCall toolCall)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(toolCall);
        var list = State.Messages.ToList();
        var idx = list.FindIndex(m => string.Equals(m.Id, messageId, StringComparison.Ordinal));
        if (idx < 0)
        {
            list.Add(new ChatMessage(messageId, "assistant", "", ToolCalls: [toolCall], Status: "streaming"));
        }
        else
        {
            var cur = list[idx];
            var calls = (cur.ToolCalls ?? []).ToList();
            var cIdx = calls.FindIndex(c => string.Equals(c.CallId, toolCall.CallId, StringComparison.Ordinal));
            if (cIdx >= 0) { calls[cIdx] = toolCall; }
            else { calls.Add(toolCall); }
            list[idx] = cur with { ToolCalls = [.. calls] };
        }
        var next = State with { Version = State.Version + 1, Messages = [.. list] };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<UiChatThreadState> SetContext(
        IReadOnlyList<ChatContextItem> contextItems,
        string? contextSummary,
        long totalInputTokens,
        long totalOutputTokens)
    {
        var next = State with
        {
            Version = State.Version + 1,
            ContextItems = contextItems is not null ? [.. contextItems] : [],
            ContextSummary = contextSummary,
            TotalInputTokens = Math.Max(0, totalInputTokens),
            TotalOutputTokens = Math.Max(0, totalOutputTokens),
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<UiChatThreadState> SetWorking(bool working, string? error = null)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Working = working,
            CanSend = !working,
            Error = error,
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<UiChatThreadState> Submit(string text, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        if (!State.CanSend)
        {
            throw new InvalidOperationException("Cannot submit while a reply is already running.");
        }
        var trimmed = text?.Trim() ?? "";
        var files = attachments is not null ? [.. attachments] : Array.Empty<ChatAttachment>();
        if (trimmed.Length == 0 && files.Length == 0) { return State; }

        var next = State with { Version = State.Version + 1, Error = null };
        await Save(next, Changed(next), new ChatMessageSubmitted(Id.FullKey(), trimmed, files));
        return next;
    }

    public async Task<UiChatThreadState> Stop()
    {
        var next = State with { Version = State.Version + 1, Working = false, CanSend = true };
        await Save(next, Changed(next), new ChatStopRequested(Id.FullKey()));
        return next;
    }

    public async Task<UiChatThreadState> Retry(string? messageId = null)
    {
        var next = State with { Version = State.Version + 1, Error = null };
        await Save(next, Changed(next), new ChatRetryRequested(Id.FullKey(), messageId));
        return next;
    }

    public async Task<UiChatThreadState> Compact()
    {
        var next = State with { Version = State.Version + 1 };
        await Save(next, Changed(next), new ChatCompactRequested(Id.FullKey()));
        return next;
    }

    public async Task<UiChatThreadState> SelectModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var next = State with { Version = State.Version + 1, ActiveModel = modelId.Trim() };
        await Save(next, Changed(next), new ChatModelSelected(Id.FullKey(), next.ActiveModel));
        return next;
    }

    public Task<UiChatThreadState> Read() => Task.FromResult(State);

    private ChatThreadChanged Changed(UiChatThreadState s)
        => new(Id.FullKey(), s.Version, s.Messages.Length, s.Working);
}
