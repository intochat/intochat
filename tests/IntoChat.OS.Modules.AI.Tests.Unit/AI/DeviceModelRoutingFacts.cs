using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class DeviceModelRoutingFacts
{
    private const string DefaultCloudChat = "deepseek/deepseek-v4.1-flash";

    [Fact]
    public async Task AnOnlineDeviceAnswersAndTheSaidNamesIt()
    {
        await using var world = await AiWorld.With(
            route: FakeRoute.Online("ada", "vlad-pc", "qwen2.5-coder:14b", answer: "hi"),
            cloudChat: DefaultCloudChat);

        var (said, _) = await world.Turn("ada", "device:vlad-pc/qwen2.5-coder:14b");

        Assert.NotNull(said);
        Assert.Equal(("hi", "device:vlad-pc/qwen2.5-coder:14b"), (said!.Text, said.Model!.Value.Value));
        Assert.Equal(0, world.CloudCalls);
    }

    [Fact]
    public async Task AnOfflineDeviceFallsBackToTheCloudRoleModel()
    {
        await using var world = await AiWorld.With(
            route: FakeRoute.Offline("ada", "vlad-pc"),
            cloudChat: DefaultCloudChat);

        var (said, _) = await world.Turn("ada", "device:vlad-pc/qwen2.5-coder:14b");

        Assert.NotNull(said);
        Assert.Equal(("cloud-answer", DefaultCloudChat), (said!.Text, said.Model!.Value.Value));
        Assert.Equal(1, world.CloudCalls);
        Assert.Contains(world.Logs, line => line.Contains("device-fallback", StringComparison.Ordinal) && line.Contains("offline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASilentDeviceFallsBack()
    {
        var route = FakeRoute.FailingBeforeFirstDelta("ada", "vlad-pc", "qwen2.5-coder:14b", new DeviceUnavailableException("no first delta in 10 s"));
        await using var world = await AiWorld.With(route: route, cloudChat: DefaultCloudChat);

        var (said, _) = await world.Turn("ada", "device:vlad-pc/qwen2.5-coder:14b");

        Assert.NotNull(said);
        Assert.Equal(("cloud-answer", DefaultCloudChat), (said!.Text, said.Model!.Value.Value));
        Assert.Equal(1, world.CloudCalls);
        Assert.Contains(world.Logs, line => line.Contains("device-fallback", StringComparison.Ordinal) && line.Contains("no first delta in 10 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureAfterTheFirstDeltaIsAnErrorNotAFallback()
    {
        var route = FakeRoute.FailingAfterFirstDelta("ada", "vlad-pc", "qwen2.5-coder:14b", firstDelta: "h", new DeviceUnavailableException("device disconnected mid-stream"));
        await using var world = await AiWorld.With(route: route, cloudChat: DefaultCloudChat);

        var (said, view) = await world.Turn("ada", "device:vlad-pc/qwen2.5-coder:14b");

        Assert.Null(said);
        Assert.Equal(0, world.CloudCalls);
        var failed = Assert.Single(view.Runs);
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal("h", failed.Reply);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
    }

    [Theory]
    [InlineData("device:/x")]
    [InlineData("device:a b/x")]
    [InlineData("devic:a/x")]
    [InlineData("device:vlad-pc/")]
    [InlineData("")]
    public void MalformedDeviceIdsDoNotParse(string id)
        => Assert.False(DeviceModelRef.TryParse(new ModelId(id), out _));

    [Fact]
    public void DeviceModelRefRoundTripsValidIdsAndRejectsOverlongDeviceName()
    {
        Assert.True(DeviceModelRef.TryParse(new ModelId("device:vlad-pc/qwen2.5-coder:14b"), out var parsed));
        Assert.Equal("vlad-pc", parsed.Device);
        Assert.Equal(new ModelId("qwen2.5-coder:14b"), parsed.Model);
        Assert.Equal(new ModelId("device:vlad-pc/qwen2.5-coder:14b"), parsed.ToModelId());

        var overlong = "device:" + new string('a', 65) + "/model";
        Assert.False(DeviceModelRef.TryParse(new ModelId(overlong), out _));
    }

    [Fact]
    public async Task LlmNeuronRoutesOnlineAndFallsBackBeforeFirstDelta()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var online = await AiWorld.With(
            route: FakeRoute.Online("ada", "vlad-pc", "qwen2.5-coder:14b", answer: "from-pc"),
            cloudChat: DefaultCloudChat);

        using (online.AsPerson("ada"))
        {
            var llm = online.Llm("ada");
            var res = await llm.Generate(
                new InferenceRequest([new AiMessage("user", [new AiText("hi")])], new ModelSelection("device:vlad-pc/qwen2.5-coder:14b")),
                ct);
            Assert.Equal("device:vlad-pc/qwen2.5-coder:14b", res.Model);
            Assert.Equal("from-pc", res.Messages.Single().Content.OfType<AiText>().Single().Text);
        }

        await using var silent = await AiWorld.With(
            route: FakeRoute.FailingBeforeFirstDelta("ada", "vlad-pc", "qwen2.5-coder:14b", new DeviceUnavailableException("no pickup in 5 s")),
            cloudChat: DefaultCloudChat);

        using (silent.AsPerson("ada"))
        {
            var llm = silent.Llm("ada");
            var chunks = new List<InferenceUpdate>();
            await foreach (var update in llm.GenerateStreaming(
                new InferenceRequest([new AiMessage("user", [new AiText("hi")])], new ModelSelection("device:vlad-pc/qwen2.5-coder:14b")),
                ct))
            {
                chunks.Add(update);
            }
            Assert.Contains(chunks, u => u.Model == DefaultCloudChat);
            Assert.Equal(1, silent.CloudCalls);
        }
    }

    [Fact]
    public async Task CatalogListsOnlineDeviceModelsAndPickKeepsOfflineDeviceOverride()
    {
        await using var world = await AiWorld.With(
            route: FakeRoute.Online("ada", "vlad-pc", "qwen2.5-coder:14b", answer: "hi"),
            cloudChat: DefaultCloudChat);

        using (world.AsPerson("ada"))
        {
            var catalog = world.Catalog("ada");
            var cards = await catalog.List();
            var deviceCard = Assert.Single(cards, c => c.Id.Value == "device:vlad-pc/qwen2.5-coder:14b");
            Assert.Equal(Where.Device, deviceCard.Where);

            await catalog.SetRole(ModelRole.Chat, new ModelId("device:offline-pc/qwen2.5-coder:14b"));
            var picked = await catalog.Pick(ModelRole.Chat);
            Assert.Equal("device:offline-pc/qwen2.5-coder:14b", picked.Value);

            var roles = await catalog.Roles();
            Assert.Equal("device:offline-pc/qwen2.5-coder:14b", roles.Device[ModelRole.Chat].Value);
            Assert.Equal(DefaultCloudChat, roles.Cloud[ModelRole.Chat].Value);
        }
    }

    [Fact]
    public async Task ATurnThatOffersToolsKeepsTheCloudModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var world = await AiWorld.With(
            route: FakeRoute.Online("ada", "vlad-pc", "qwen2.5-coder:14b", answer: "from-pc"),
            cloudChat: DefaultCloudChat);

        using (world.AsPerson("ada"))
        {
            var llm = world.Llm("ada");
            var tool = new InferenceTool("echo", "Echo text", """{"type":"object","properties":{"text":{"type":"string"}}}""");
            var res = await llm.Generate(
                new InferenceRequest(
                    [new AiMessage("user", [new AiText("hi")])],
                    new ModelSelection("device:vlad-pc/qwen2.5-coder:14b"),
                    Tools: [tool]),
                ct);
            Assert.Equal(DefaultCloudChat, res.Model);
            Assert.Equal(1, world.CloudCalls);
            Assert.Contains(world.Logs, line => line.Contains("device-fallback", StringComparison.Ordinal) && line.Contains("tools-unsupported", StringComparison.Ordinal));
        }
    }

    private sealed class AiWorld : IAsyncDisposable
    {
        private readonly TestOS _os;
        private readonly ServiceProvider _services;
        private readonly RecordingCloudClient _cloud;
        private readonly List<string> _logs;

        private AiWorld(TestOS os, ServiceProvider services, RecordingCloudClient cloud, List<string> logs)
        {
            _os = os;
            _services = services;
            _cloud = cloud;
            _logs = logs;
        }

        public int CloudCalls => _cloud.Calls;
        public IReadOnlyList<string> Logs => _logs;

        public IDisposable AsPerson(string brain)
            => TestEdge.Authority.Enter(TestEdge.Authority.Person(brain, brain, new BrainId(brain)));

        public ILLM Llm(string brain)
            => _os.Get<ILLM>(PlainNeuronContracts.IdOf(typeof(ILLM), ModelAddress.Key(brain)));

        public IModelCatalog Catalog(string brain)
            => _os.Get<IModelCatalog>(PlainNeuronContracts.IdOf(typeof(IModelCatalog), IModelCatalog.Key(brain)));

        public async Task<(Said? AssistantSaid, ConversationView View)> Turn(string brain, string model)
        {
            using var _ = AsPerson(brain);
            var convId = new NeuronId(new BrainId(brain), IConversation.Type, "conversations/c1");
            var conv = _os.Get<IConversation>(convId);
            await conv.Seat("ino", new AgentDefinition
            {
                DisplayName = "Ino",
                Instructions = "Be concise.",
                Model = new ModelSelection(model),
            });
            await conv.Say("hello", new ModelId(model));

            ConversationView view = await conv.Read();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (view.Active is not null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(15);
                view = await conv.Read();
            }

            var reply = view.Entries.OfType<Said>().LastOrDefault(s => s.By is Participant.Assistant);
            return (reply, view);
        }

        public static Task<AiWorld> With(IDeviceChatRoute route, string cloudChat)
        {
            var logs = new List<string>();
            var cloud = new RecordingCloudClient("cloud-answer");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyCloud,
                })
                .Build();

            TestOS? osRef = null;
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(TestEdge.Authority);
            services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logs)));
            services.AddOptions<AIOptions>().Configure(o => o.Roles.Cloud[ModelRole.Chat] = cloudChat);
            services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
            services.AddSingleton<IPriceList, ModelPriceList>();
            services.AddSingleton<IChatClient>(cloud);
            services.AddSingleton<IDeviceChatRoute>(route);
            services.AddSingleton<ISecretRedactor>(new PassthroughRedactor());
            services.AddSingleton<INeuronProxies>(_ => new TestOSProxies(osRef!));
            services.AddSingleton<TimeProvider>(_ => osRef!.Clock);
            services.AddSingleton<IModelInference, InferenceService>();
            AIClients.Add(services);
            var sp = services.BuildServiceProvider();

            var os = TestOS.Local(o => o.Services = sp);
            osRef = os;
            os.Register(typeof(AIModule).Assembly);
            IntoChat.OS.Modules.Apps.CatalogFamily.RegisterCatalog(os);

            return Task.FromResult(new AiWorld(os, sp, cloud, logs));
        }

        public async ValueTask DisposeAsync()
        {
            await _os.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class FakeRoute : IDeviceChatRoute
    {
        private readonly string _owner;
        private readonly string _device;
        private readonly string? _model;
        private readonly Func<IChatClient>? _clientFactory;

        private FakeRoute(string owner, string device, string? model, Func<IChatClient>? clientFactory)
        {
            _owner = owner;
            _device = device;
            _model = model;
            _clientFactory = clientFactory;
        }

        public static FakeRoute Online(string owner, string device, string model, string answer)
            => new(owner, device, model, () => new ScriptedChatClient([answer], null));

        public static FakeRoute Offline(string owner, string device)
            => new(owner, device, null, null);

        public static FakeRoute FailingBeforeFirstDelta(string owner, string device, string model, Exception error)
            => new(owner, device, model, () => new ScriptedChatClient([], error));

        public static FakeRoute FailingAfterFirstDelta(string owner, string device, string model, string firstDelta, Exception error)
            => new(owner, device, model, () => new ScriptedChatClient([firstDelta], error));

        public Task<ModelCard[]> Models(BrainId owner, CancellationToken ct)
        {
            if (!string.Equals(owner.Value, _owner, StringComparison.Ordinal) || _model is null || _clientFactory is null)
            {
                return Task.FromResult(Array.Empty<ModelCard>());
            }

            var id = new DeviceModelRef(_device, new ModelId(_model)).ToModelId();
            var card = new ModelCard(id, AiProvider.Device, "qwen", _model, Where.Device, Abilities.Text, Tier.Balanced);
            return Task.FromResult(new[] { card });
        }

        public Task<IChatClient?> Chat(BrainId owner, DeviceModelRef model, CancellationToken ct)
        {
            if (!string.Equals(owner.Value, _owner, StringComparison.Ordinal)
                || !string.Equals(model.Device, _device, StringComparison.Ordinal)
                || _model is null
                || !string.Equals(model.Model.Value, _model, StringComparison.Ordinal)
                || _clientFactory is null)
            {
                return Task.FromResult<IChatClient?>(null);
            }

            return Task.FromResult<IChatClient?>(_clientFactory());
        }
    }

    private sealed class ScriptedChatClient(string[] deltas, Exception? error) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (error is not null)
            {
                return Task.FromException<ChatResponse>(error);
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(deltas))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var delta in deltas)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
            }
            if (error is not null)
            {
                throw error;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingCloudClient(string reply) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = options?.ModelId });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply) { ModelId = options?.ModelId };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class PassthroughRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken ct) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class TestOSProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
        public object Get(Type contract, NeuronId id) => throw new NotSupportedException();
    }

    private sealed class ListLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(logs);
        public void Dispose() { }

        private sealed class ListLogger(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (logs) { logs.Add(formatter(state, exception)); }
            }
        }
    }
}
