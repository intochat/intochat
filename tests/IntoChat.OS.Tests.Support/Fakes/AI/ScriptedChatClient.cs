using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Support.Fakes.AI;

/// <summary>
/// An <see cref="IChatClient"/> driven deterministically by an <see cref="LlmScript"/>.
/// Supports both buffered (<see cref="GetResponseAsync"/>) and streaming (<see cref="GetStreamingResponseAsync"/>) turns.
/// </summary>
public sealed class ScriptedChatClient(LlmScript script) : IChatClient
{
    private static readonly ChatClientMetadata Metadata = new("scripted", defaultModelId: "scripted");

    private readonly LlmScript _script = script ?? throw new ArgumentNullException(nameof(script));

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contents = ExecuteTurn(messages);
        var reply = new ChatMessage(ChatRole.Assistant, contents);
        return Task.FromResult(new ChatResponse(reply) { ModelId = "scripted" });
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contents = ExecuteTurn(messages);
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, contents) { ModelId = "scripted" };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) { return null; }
        if (serviceType == typeof(ChatClientMetadata)) { return Metadata; }
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Registered as a singleton across turns; AgentTurnLoop disposes clients after each turn.
    }

    private List<AIContent> ExecuteTurn(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var history = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        foreach (var message in history)
        {
            if (message.Role != ChatRole.Tool) { continue; }
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                if (!string.IsNullOrWhiteSpace(result.CallId))
                {
                    _script.RecordToolResult(result.CallId, FormatResult(result.Result));
                }
            }
        }

        var lastUserText = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var turn = _script.TakeNext(lastUserText);

        var contents = new List<AIContent>(turn.ToolCalls.Count + turn.Texts.Count);
        foreach (var call in turn.ToolCalls)
        {
            var callId = _script.RegisterPendingCall(call.Name, call.ArgumentsJson);
            contents.Add(new FunctionCallContent(callId, call.Name, call.Arguments));
        }
        foreach (var text in turn.Texts)
        {
            contents.Add(new TextContent(text));
        }

        return contents;
    }

    private static string FormatResult(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result),
    };
}
