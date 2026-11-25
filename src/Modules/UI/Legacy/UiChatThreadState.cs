namespace IntoChat.OS.Modules.UI.Legacy;

// The state the old ui.chat neuron stored, unchanged so a stored thread still loads.

[GenerateSerializer, Alias("ui.chat.tool-call")]
public sealed record ChatToolCall(
    [property: Id(0)] string CallId,
    [property: Id(1)] string Name,
    [property: Id(2)] string ArgumentsJson,
    [property: Id(3)] string? ResultJson = null,
    [property: Id(4)] string Status = "running",
    [property: Id(5)] long? DurationMs = null)
{
    public ChatToolCall() : this("", "", "{}", null, "running", null) { }
}

[GenerateSerializer, Alias("ui.chat.attachment")]
public sealed record ChatAttachment(
    [property: Id(0)] string Name,
    [property: Id(1)] string ContentType,
    [property: Id(2)] string? NeuronUri = null)
{
    public ChatAttachment() : this("", "application/octet-stream", null) { }
}

[GenerateSerializer, Alias("ui.chat.slot")]
public sealed record ChatInlineSlot(
    [property: Id(0)] string Kind,
    [property: Id(1)] string NeuronKey)
{
    public ChatInlineSlot() : this("ui.chart", "") { }
}

[GenerateSerializer, Alias("ui.chat.message")]
public sealed record ChatMessage(
    [property: Id(0)] string Id,
    [property: Id(1)] string Role,
    [property: Id(2)] string Text,
    [property: Id(3)] string Reasoning = "",
    [property: Id(4)] ChatToolCall[]? ToolCalls = null,
    [property: Id(5)] ChatAttachment[]? Attachments = null,
    [property: Id(6)] ChatInlineSlot[]? Slots = null,
    [property: Id(7)] string Status = "done",
    [property: Id(8)] string? Error = null,
    [property: Id(9)] long InputTokens = 0,
    [property: Id(10)] long OutputTokens = 0)
{
    public ChatMessage() : this("", "assistant", "", "", null, null, null, "done", null, 0, 0) { }
}

[GenerateSerializer, Alias("ui.chat.context-item")]
public sealed record ChatContextItem(
    [property: Id(0)] string Name,
    [property: Id(1)] string Summary,
    [property: Id(2)] string Trust = "platform",
    [property: Id(3)] int TokenEstimate = 0)
{
    public ChatContextItem() : this("", "", "platform", 0) { }
}

[GenerateSerializer, Alias("ui.chat.model-option")]
public sealed record ChatModelOption(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string Provider = "")
{
    public ChatModelOption() : this("", "", "") { }
}

[GenerateSerializer, Alias("ui.chat.state")]
public sealed record UiChatThreadState(
    [property: Id(0)] int Version,
    [property: Id(1)] string ConversationId,
    [property: Id(2)] string Title,
    [property: Id(3)] ChatMessage[] Messages,
    [property: Id(4)] string ActiveModel,
    [property: Id(5)] ChatModelOption[] AvailableModels,
    [property: Id(6)] bool Working,
    [property: Id(7)] bool CanSend,
    [property: Id(8)] ChatContextItem[] ContextItems,
    [property: Id(9)] string? ContextSummary,
    [property: Id(10)] long TotalInputTokens,
    [property: Id(11)] long TotalOutputTokens,
    [property: Id(12)] long ContextBudgetTokens,
    [property: Id(13)] bool VoiceAvailable,
    [property: Id(14)] string? Error)
{
    public const int MaxMessages = 500;

    public UiChatThreadState() : this(
        0,
        "default",
        "New conversation",
        [],
        "",
        [],
        false,
        true,
        [],
        null,
        0,
        0,
        128_000,
        false,
        null)
    {
    }
}
