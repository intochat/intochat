using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Media;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;

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

    public async Task<AgentConversationState> ReadConversation(string threadId, CancellationToken ct = default)
        => await (await Conversation(threadId)).ReadConversation(ct);

    public Task<ModelCatalog> Models() => new AssistantTurnExecution(services, GrainFactory).Models();

    public Task<IAgent> Conversation(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > 200 ||
            threadId.Any(character => char.IsControl(character) || character is '/' or '\\'))
        { throw new ArgumentException("A bounded thread identifier is required.", nameof(threadId)); }
        return Task.FromResult(GrainFactory.GetGrain<IAgent>(AssistantConversations.Key(Workspace, threadId)));
    }

    public Task<AgentDefinition> DefineTurn(string? summary)
    {
        var registered = services.GetServices<IAgentToolFactory>()
            .SelectMany(factory => factory.Create(() => throw new InvalidOperationException("No tool call is active.")))
            .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var definition = Snapshot.Definition ?? AssistantDefinition.For(options: services.GetRequiredService<IOptions<AssistantOptions>>().Value);
        if (Snapshot.Definition is null)
        {
            var native = services.GetService<NativeTools>();
            definition = definition with
            {
                Instructions = definition.Instructions + $"\nThe user's workspace is the ui.workspace neuron \"{Workspace}\".",
                // Optional host modules may be absent. Their default tools must not
                // make an otherwise standalone Assistant unusable. App-declared
                // tools can also come from dynamic sources resolved by the runner.
                Tools = definition.Tools.Where(tool => registered.Contains(tool) || native?.Contains(tool) == true).ToArray(),
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
