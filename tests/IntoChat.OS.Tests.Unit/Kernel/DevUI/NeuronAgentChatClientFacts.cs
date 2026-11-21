using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Unit.DevUI;

// DevUI talks to the session list's current conversation via IConversation.Say + Read(since).
public sealed class NeuronAgentChatClientFacts
{
    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class NoProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });
        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class StubChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private sealed class FixedAmbient(Caller caller) : ICallerAmbient
    {
        public Caller? Current => caller;
    }

    [Fact]
    public void TextStreamsThrough() => Assert.Equal("hi", NeuronAgentChatClient.Update(new AgentTurnEvent.TextChunk("hi"))!.Text);

    [Fact]
    public void AToolCallIsAStatusLine()
    {
        var update = NeuronAgentChatClient.Update(new AgentTurnEvent.ToolStarted("c1", "neurons_call", "{}"))!;
        Assert.Contains("neurons_call", Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedTurnFailsTheResponse()
        => Assert.Throws<InvalidOperationException>(() => NeuronAgentChatClient.Update(new AgentTurnEvent.TurnFailed("no model")));

    [Fact]
    public void BookkeepingShowsNothing() => Assert.Null(NeuronAgentChatClient.Update(new AgentTurnEvent.Finished()));

    [Fact]
    public async Task DevUiTalksToTheCurrentConversation()
    {
        var brain = new BrainId("owner");
        var sc = new ServiceCollection();
        sc.AddSingleton<ISecretRedactor, NoSecrets>();
        sc.AddSingleton<IAiCredentials, NoProviders>();
        sc.AddSingleton(new IntoChat.OS.Host.Surface.OsNeuronSurface(null!, null!, null!));
        sc.AddSingleton<IChatClient>(new StubChatClient("Hello from conversation."));
        sc.AddOptions<AIOptions>();
        sc.AddSingleton<ModelCatalog>();
        sc.AddSingleton(TimeProvider.System);
        var services = sc.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        os.Register(typeof(AIModule).Assembly);
        os.Register<SessionListNeuron>();

        var personCaller = os.Edge.Person("owner", "owner", brain);
        using var scope = os.Edge.Enter(personCaller);

        var sessions = os.Get<ISessionList>(new NeuronId(brain, Sessions.Type, ISessionList.DefaultKey));
        var convId = "c-" + new string('d', 32);
        await sessions.Open(convId);
        var conversation = os.Get<IConversation>(new NeuronId(brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse(convId))));
        await conversation.Seat("ino", new AgentDefinition { Instructions = "devui" });

        var clientServices = new ServiceCollection()
            .AddSingleton<ICallerAmbient>(new FixedAmbient(personCaller))
            .BuildServiceProvider();
        using var client = new NeuronAgentChatClient(
            clientServices,
            os.Get<ISessionList>,
            os.Get<IConversation>,
            TimeSpan.FromMilliseconds(10));

        var responseTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi from devui")], cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 60 && !responseTask.IsCompleted; i++)
        {
            await os.Idle();
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        var response = await responseTask;
        Assert.Equal("Hello from conversation.", response.Text);

        var view = await conversation.Read();
        var said = view.Entries.OfType<Said>().ToArray();
        Assert.Equal(2, said.Length);
        Assert.IsType<Participant.Person>(said[0].By);
        Assert.Equal("hi from devui", said[0].Text);
        Assert.IsType<Participant.Agent>(said[1].By);
        Assert.Equal("Hello from conversation.", said[1].Text);
    }
}
