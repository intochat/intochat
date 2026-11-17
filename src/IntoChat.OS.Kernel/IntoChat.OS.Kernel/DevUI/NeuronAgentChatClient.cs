using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Kernel.DevUI;

// DevUI's view of the caller's current Ino conversation: the same IAgent neuron Ino talks to, so a chat started here shows
// in Ino and the transcript lives in one place (the agent's state). Only the newest user message is sent; the agent keeps
// the history. Tool calls and approvals stay the agent's own; they appear here as short status lines.
internal sealed class NeuronAgentChatClient(IServiceProvider services) : IChatClient
{
    internal const string DefaultConversation = "ino/conversations/main";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) { updates.Add(update); }
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(text)) { yield break; }
        var key = await ConversationKey();
        (services.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as Microsoft.Extensions.Logging.ILoggerFactory)?
            .CreateLogger<NeuronAgentChatClient>().LogInformation("DevUI turn for agent {Agent} (caller brain {Brain}).", key, (services.GetService(typeof(DigitalBrain.Access.ICallerAmbient)) as DigitalBrain.Access.ICallerAmbient)?.Current?.Brain.Value);
        var agent = SiloNeurons.Get<IAgent>(services, key);
        await foreach (var turn in agent.Run(AgentTurn.Text(text), cancellationToken))
        {
            if (Update(turn) is { } update) { yield return update; }
        }
    }

    // The newest conversation Ino uses for this brain; the main one when Ino has none yet.
    private async Task<DigitalBrain.NeuronId> ConversationKey()
    {
        var brain = ((services.GetService(typeof(DigitalBrain.Access.ICallerAmbient)) as DigitalBrain.Access.ICallerAmbient)?.Current ?? throw new UnauthorizedAccessException("The request carries no caller.")).Brain.Value;
        var current = await SiloNeurons.Get<IInoConversations>(services, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "ino.conversations", IInoConversations.DefaultKey)).Current();
        var key = current.FirstOrDefault(item => item.Length > 0) ?? DefaultConversation;
        return new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "agent", key.StartsWith(brain + "/", StringComparison.Ordinal) ? key[(brain.Length + 1)..] : key);
    }

    internal static ChatResponseUpdate? Update(AgentTurnEvent turn) => turn switch
    {
        AgentTurnEvent.TextChunk chunk => new ChatResponseUpdate(ChatRole.Assistant, chunk.Content),
        AgentTurnEvent.ToolStarted started => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"Using {started.Name} {started.Arguments}")]),
        AgentTurnEvent.ToolFailed failed => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"{failed.Name} failed: {failed.Message}")]),
        AgentTurnEvent.TurnFailed failed => throw new InvalidOperationException(failed.Message),
        _ => null,
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
