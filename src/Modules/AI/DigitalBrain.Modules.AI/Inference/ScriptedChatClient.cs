using DigitalBrain.AI.Scripted;
using Microsoft.Extensions.AI;

namespace DigitalBrain.AI;

// The agent loop speaks IChatClient; a scripted model speaks ILLM. This adapter lets the publish
// gate drive whole tool-calling turns through IScriptedLLM without a provider.
internal sealed class ScriptedChatClient(IScriptedLLM model) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await model.Generate(new InferenceRequest([.. messages.Select(InferenceMapping.FromChatMessage)]), cancellationToken);
        return new ChatResponse([.. result.Messages.Select(InferenceMapping.ToChatMessage)]) { FinishReason = ChatFinishReason.Stop, ModelId = result.Model };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
        { yield return update; }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
