using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Conversations;

/// <summary>
/// DigitalBrain reactive projection that keeps <see cref="ISessionList"/> synchronized
/// with human activity published by <see cref="IConversation"/> via <see cref="ConversationChanged"/>.
/// </summary>
[Neuron(NeuronType)]
[Reacts(typeof(IConversation), typeof(ConversationChanged))]
internal sealed class SessionListProjectionNeuron(ILogger<SessionListProjectionNeuron>? logger = null)
    : Neuron
{
    internal const string NeuronType = "conversation.session-list";

    private ILogger Logger => (ILogger?)logger ?? OsConsoleLog.For<SessionListProjectionNeuron>();

    [On]
    public async Task On(ConversationChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (changed.ActivityText is null || !Sessions.IsId(changed.Id.Value))
        {
            return;
        }

        try
        {
            await Context.Get<ISessionList>(new NeuronId(Id.Brain, Sessions.Type, ISessionList.DefaultKey))
                .Touch(changed.Id.Value, changed.ActivityText);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogDebug(error, "Session list could not be touched for conversation {ConversationId}.", changed.Id);
        }
    }
}
