using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Modules.Assistant.Tests;

// Echoes the user's message, so a feature fact can see its turn reach the model and settle.
internal sealed class ScriptedAssistantModel : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var asked = messages.Last(message => message.Role == ChatRole.User).Text;
        return Reply(new ChatMessage(ChatRole.Assistant, "You said: " + asked));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) { yield return update; }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
    private static Task<ChatResponse> Reply(ChatMessage message) => Task.FromResult(new ChatResponse(message));
}
