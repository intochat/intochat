using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Capabilities.Secrets;

namespace IntoChat.OS.Modules.AI.Tests;

// Integration tier: the plain AI neurons on TestOS and on SiloHost.Local, the model neurons over a gated fake inference.
public sealed class AiFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    public GatedInference Inference { get; } = new();

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(AIModule), typeof(IntoChat.OS.Modules.Identity.IdentityModule)];

    protected override void ConfigureTestOS(IServiceCollection services)
    {
        services.AddSingleton<IntoChat.OS.Modules.AI.Models.IModelInference>(Inference);
        services.AddSingleton<IntoChat.OS.Modules.AI.Memory.IMemoryEmbedder>(new IntoChat.OS.Modules.AI.Memory.HybridMemoryEmbedder());
        services.AddSingleton<ISecretRedactor, NoSecrets>();
        services.AddSingleton(TimeProvider.System);
    }

    // The AI neurons' [Signal] records live in the contracts assembly, beside the module's.
    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.IAgent).Assembly);
        // ConversationChanged lives in Shell.Contracts; ConversationNeuron lives in AIModule.
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Conversations.ConversationChanged).Assembly);
        // The security posture neuron listens to the identity module's grants signal.
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Identity.Contracts.Grants.GrantsChanged).Assembly);
        services.AddSingleton<IntoChat.OS.Modules.AI.Models.IModelInference>(Inference);
        services.AddSingleton<IntoChat.OS.Modules.AI.Memory.IMemoryEmbedder>(new IntoChat.OS.Modules.AI.Memory.HybridMemoryEmbedder());
        services.AddSingleton<ISecretRedactor, NoSecrets>();
    }

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);

        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }
}
