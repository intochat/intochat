using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Conversations;

public sealed class SquadRoutingFacts
{
    private static readonly BrainId Brain = new("owner");

    private sealed class NoOpSecretRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class NoOpAiCredentials : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });
        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class PingPongChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, string> responder) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responder(messages, options))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = responder(messages, options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task SquadRoutesMentionToSpecialist_DefaultsToLead_AndCapsHandoffLoopAtMaxDepthThree()
    {
        // Each agent mentions the other to attempt an infinite ping-pong loop; maxDepth = 3 must stop it at 3 turns!
        var client = new PingPongChatClient((messages, options) =>
        {
            var sys = (options?.Instructions ?? "") + "\n" + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            return sys.Contains("CODER_PERSONA", StringComparison.Ordinal)
                ? "Coder done, handing off to @architect"
                : "Architect done, handing off to @coder";
        });

        var sc = new ServiceCollection()
            .AddSingleton<ISecretRedactor>(new NoOpSecretRedactor())
            .AddSingleton<IAiCredentials>(new NoOpAiCredentials())
            .AddSingleton(new NeuronSurface())
            .AddSingleton<IChatClient>(client)
            .AddSingleton<ModelCatalog>()
            .AddSingleton(TimeProvider.System);
        sc.AddOptions<AIOptions>();
        var services = sc.BuildServiceProvider();

        await using var os = TestOS.Local(options => options.Services = services)
            .Register<ConversationNeuron>();

        var convId = new NeuronId(Brain, IConversation.Type, "conversations/squad-1");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var conv = os.Get<IConversation>(convId);
            await conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "Engineering Squad", leadAgentId: "architect");
            await conv.Seat("architect", new AgentDefinition { DisplayName = "Architect", Instructions = "ARCHITECT_PERSONA" });
            await conv.Seat("coder", new AgentDefinition { DisplayName = "Coder", Instructions = "CODER_PERSONA" });

            // Explicit @coder mention routes first turn to coder, then coder -> @architect -> @coder, stopping at maxDepth = 3.
            await conv.Say("@coder please implement the feature");
            await os.Idle();

            var view = await conv.Read();
            var replies = view.Entries.OfType<Said>().Where(static s => s.By is Participant.Assistant).ToArray();
            Assert.Equal(SharedRoomAgentPolicy.MaxSquadHandoffDepth, replies.Length);
            Assert.Equal(["coder", "architect", "coder"], replies.Select(static r => ((Participant.Assistant)r.By).AgentId));
        }
    }
}
