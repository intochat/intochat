using System.Runtime.CompilerServices;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Media;
using DigitalBrain.AI.Models;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;

namespace DigitalBrain.Assistant;


// Runs, transcriptions and model reads may overlap: each is independent of the others.
[Reentrant, GrainType("apps.assistant")]
internal sealed class AssistantNeuron(
    [PersistentState("apps.assistant", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<AssistantState> store, IServiceProvider services)
    : Neuron<AssistantState>(store), IAssistant
{
    private string Key => this.GetPrimaryKeyString();
    // The model catalog is deployment-wide and already secret-free; the HTTP posture is its
    // gate. Everything else belongs to the assistant's own workspace.
    public override DigitalBrain.Framework.NeuronAccess Access(string operation)
        => operation is nameof(Models)
            ? DigitalBrain.Framework.NeuronAccess.PublicOperation
            : new(Workspace);
    public Task<AssistantState> Read() => Task.FromResult(Snapshot);
    // An assistant started for a workspace is keyed "{workspace}/applications/assistant".
    private string Workspace => Key.Split("/applications/")[0];
    public async IAsyncEnumerable<string> Run(AssistantRun request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in new AssistantTurnExecution(services, GrainFactory).Run(Workspace, request, DefineTurn, ct))
        { yield return item; }
    }

    public Task<AssistantTranscriptionResult> Transcribe(string? base64, CancellationToken ct = default)
        => new AssistantTranscription(GrainFactory.GetGrain<ISpeechRecognizer>(Key + "/voice")).Transcribe(base64, ct);

    public async Task<AgentState> ReadConversation(string threadId, CancellationToken ct = default)
        => await (await Conversation(threadId)).Read();

    public Task<ModelChoices> Models() => new AssistantTurnExecution(services, GrainFactory).Models();

    public Task<IAgent> Conversation(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > 200 ||
            threadId.Any(character => char.IsControl(character) || character is '/' or '\\'))
        { throw new ArgumentException("A bounded thread identifier is required.", nameof(threadId)); }
        return Task.FromResult(GrainFactory.GetGrain<IAgent>(AssistantConversations.Key(Workspace, threadId)));
    }

    public Task<AgentDefinition> DefineTurn(string? summary)
    {
        var definition = Snapshot.Definition ?? AssistantDefinition.For(options: services.GetRequiredService<IOptions<AssistantOptions>>().Value);
        if (Snapshot.Definition is null)
        {
            definition = definition with
            {
                Instructions = definition.Instructions + $"\nThe user's workspace is the ui.workspace neuron \"{Workspace}\".",
            };
        }
        if (!string.IsNullOrEmpty(summary))
        { definition = definition with { Instructions = definition.Instructions + "\nEarlier conversation summary: " + summary }; }
        return Task.FromResult(definition);
    }

    public async Task Configure(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await Save(Snapshot with { Definition = definition }, new AssistantConfigured(Key));
    }
}
