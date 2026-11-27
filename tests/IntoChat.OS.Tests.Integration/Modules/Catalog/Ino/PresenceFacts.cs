using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Flows;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tests.Integration.Flows;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Ino;

// Publishes an agent's turn signals as the agent neuron does, for the conversation agent the facts name.
public interface IAgentSignalPump
{
    Task Started(string agentId, string runId, string? traceId);

    Task Tool(string agentId, string runId, string tool, string arguments, int sequence);
}

[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class AgentSignalPump : DigitalBrain.Neurons.Neuron<PumpState>, IAgentSignalPump
{
    public const string Alias = "test.agent-pump";

    public Task Started(string agentId, string runId, string? traceId) => Send(new TurnStarted(agentId, runId, "hi") { TraceId = traceId });

    public Task Tool(string agentId, string runId, string tool, string arguments, int sequence)
        => Send(new TurnProgressed(agentId, runId, "tool-call", null, tool, arguments, sequence));

    private Task Send(Signal signal) => Save(State with { Sent = State.Sent + 1 }, signal);
}

[DigitalBrain.Signal("test.presence.worked")]
public sealed record Worked(int Count) : Signal;

public interface IPresenceWorker
{
    Task Work();
}

// A neuron doing its part of a turn: it publishes on the trace the call carries.
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class PresenceWorker : DigitalBrain.Neurons.Neuron<PumpState>, IPresenceWorker
{
    public const string Alias = "test.presence-worker";

    public Task Work() => Save(State with { Sent = State.Sent + 1 }, new Worked(State.Sent + 1));
}

// Ino's presence on a real silo: the fold over agent, context, approval and flow signals.
public sealed class PresenceFacts
{
    private const string Agent = "ino/conversations/main";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class PresenceFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [.. CatalogFamily.Modules, typeof(AIModule), typeof(FlutterModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.AddJournalPayloads(typeof(TurnStarted).Assembly);
            services.AddJournalPayloads(typeof(ConversationChanged).Assembly);
            services.TryAddSingleton(TimeProvider.System);
        }
    }

    private static IPresence Presence(INeuronRuntime runtime) => runtime.Get<IPresence>(IPresence.Key(runtime.Brain.Value));

    private static IAgentSignalPump Pump(INeuronRuntime runtime) => runtime.Get<IAgentSignalPump>(new NeuronId(runtime.Brain, AgentSignalPump.Alias, "pump"));

    private static IAgentContext ContextOf(INeuronRuntime runtime, string name) => runtime.Get<IAgentContext>(new NeuronId(runtime.Brain, "agent-context", "context/" + name));

    private static IFocus FocusTool(INeuronRuntime runtime) => runtime.Get<IFocus>(new NeuronId(runtime.Brain, "ino.focus", IFocus.DefaultKey));

    private static IDisposable As(INeuronRuntime runtime, Who kind)
        => runtime.As(kind, app: "test-app");

    // An agent's tool call as AgentTools.Marked stamps it: the person's principal, the agent and the conversation.
    private static IDisposable AsInoTool(INeuronRuntime runtime)
        => runtime.As(Who.Agent, conversation: Agent);

    // Makes "main" the current conversation and activates the presence, so its synapses hold before the facts publish.
    private static async Task<INeuronRuntime> Started(PresenceFixture fixture, Action<IServiceCollection>? siloServices = null)
    {
        var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct, siloServices: siloServices);
        using var person = runtime.As(Who.Person);
        await runtime.Get<IThreadDirectory>(new NeuronId(runtime.Brain, Threads.Type, IThreadDirectory.DefaultKey)).Open("main");
        await Presence(runtime).Read(Ct);
        return runtime;
    }

    private static async Task<Presence> Eventually(INeuronRuntime runtime, Func<Presence, bool> done)
    {
        var presence = await Presence(runtime).Read(Ct);
        for (var i = 0; i < 200 && !done(presence); i++)
        {
            await Task.Delay(50, Ct);
            presence = await Presence(runtime).Read(Ct);
        }
        Assert.True(done(presence), $"The presence did not reach the expected record; last: {presence}");
        return presence;
    }

    private static string FocusText(string target, string tone, string reason)
        => JsonSerializer.Serialize(new { target, tone, reason, action = new { kind = "open", argument = "files" } });

    private static string Inner(TestInner inner) => JsonSerializer.Serialize(inner, TestFlow.Json);

    [Fact]
    public async Task InterruptBeatsApprovalBeatsToolCall()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);

        await Pump(runtime).Started(Agent, "run-1", null);
        await Pump(runtime).Tool(Agent, "run-1", "neuron_call", """{"method":"Write"}""", 1);
        await Eventually(runtime, presence => presence.State == PresenceState.Working);

        string flowId;
        using (AsInoTool(runtime))
        {
            flowId = await runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value)).Start(TestFlow.Alias, "Report", null, Inner(new TestInner { WaitApproval = true }), Ct);
        }
        var waiting = await Eventually(runtime, presence => presence.State == PresenceState.Waiting);
        Assert.Equal(flowId, waiting.FlowId);
        Assert.Equal("second", waiting.Step);
        Assert.Equal(TestFlow.Alias, waiting.FlowType);

        using (As(runtime, Who.Person)) { await ContextOf(runtime, "security.posture").Set(new ContextEntry("stop", "Stop now.", [], [ContextTags.Interrupt])); }
        var paused = await Eventually(runtime, presence => presence.State == PresenceState.Paused);
        Assert.Equal("Stop now.", paused.Reason);

        using (As(runtime, Who.Kernel)) { await ContextOf(runtime, "security.posture").Remove("stop"); }
        await Eventually(runtime, presence => presence.State == PresenceState.Waiting);
    }

    [Fact]
    public async Task SearchToolIsSearching()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);

        await Pump(runtime).Started(Agent, "run-1", null);
        await Pump(runtime).Tool(Agent, "run-1", "neuron_call", """{"contract":"IntoChat.OS.Modules.Apps.IData","method":"Search"}""", 1);

        await Eventually(runtime, presence => presence.State == PresenceState.Searching);
    }

    [Fact]
    public async Task RunUnder2sWithoutToolIsListening()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);

        await Pump(runtime).Started("ino/conversations/other", "run-0", null);
        await Pump(runtime).Started(Agent, "run-1", null);

        var presence = await Eventually(runtime, presence => presence.State != PresenceState.Idle);
        Assert.Equal(PresenceState.Listening, presence.State);
    }

    [Fact]
    public async Task FailedFlowWithin10sIsAttention()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        string flowId;
        using (AsInoTool(runtime)) { flowId = await flows.Start(TestFlow.Alias, "Report", null, Inner(new TestInner { Wait = true }), Ct); }
        for (var i = 0; i < 200 && (await flows.Read(flowId, Ct))?.App != "assistant"; i++) { await Task.Delay(50, Ct); }

        await runtime.Get<IFlowSignalPump>(new NeuronId(runtime.Brain, FlowSignalPump.Alias, "pump")).Failed(flowId, "The source went away.");

        var presence = await Eventually(runtime, presence => presence.State == PresenceState.Attention);
        Assert.Equal(flowId, presence.FlowId);
        Assert.Equal("The source went away.", presence.Reason);
    }

    [Fact]
    public async Task FocusParsedFromUiFocusEntry()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);

        using (As(runtime, Who.App)) { await ContextOf(runtime, "ui.focus").Set(new ContextEntry("ui.focus", FocusText("window:files", "warning", "Look here."), [], [ContextTags.UiFocus])); }

        var presence = await Eventually(runtime, presence => presence.Focus is not null);
        Assert.Equal(new Focus("window:files", FocusTone.Warning, "Look here.", new FocusAction("open", "files")), presence.Focus);
    }

    [Fact]
    public async Task MalformedFocusIsDropped()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);
        var context = ContextOf(runtime, "ui.focus");
        using (As(runtime, Who.App)) { await context.Set(new ContextEntry("ui.focus", FocusText("window:files", "info", "Here."), [], [ContextTags.UiFocus])); }
        await Eventually(runtime, presence => presence.Focus is not null);

        using (As(runtime, Who.App)) { await context.Set(new ContextEntry("ui.focus", "{not json", [], [ContextTags.UiFocus])); }

        await Eventually(runtime, presence => presence.Focus is null);
    }

    [Fact]
    public async Task ModelTrustCannotSetCritical()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);
        var context = ContextOf(runtime, "ui.focus");
        using (As(runtime, Who.App)) { await context.Set(new ContextEntry("ui.focus", FocusText("window:files", "critical", "Platform says."), [], [ContextTags.UiFocus])); }
        Assert.Equal(FocusTone.Critical, (await Eventually(runtime, presence => presence.Focus is not null)).Focus!.Tone);

        using (As(runtime, Who.Agent)) { await context.Set(new ContextEntry("ui.focus", FocusText("window:files", "critical", "Model says."), [], [ContextTags.UiFocus])); }

        await Eventually(runtime, presence => presence.Focus is null);
        var entry = Assert.Single((await context.Read()).Entries);
        Assert.Equal(ContextTrust.Model, entry.Provenance!.Trust);
    }

    [Fact]
    public async Task PointWritesEntryAndNextTurnClears()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);
        await Pump(runtime).Started(Agent, "run-1", null);
        await Eventually(runtime, presence => presence.State == PresenceState.Listening);

        var result = await FocusTool(runtime).Point("kind:files.picker", FocusTone.Info, "Pick the report.", new FocusAction("open", "files"));

        Assert.True(result.Ok, result.Text);
        var entry = Assert.Single((await ContextOf(runtime, "ui.focus").Read()).Entries);
        Assert.Equal(IFocus.EntryName, entry.Name);
        Assert.Equal([ContextTags.UiFocus], entry.EffectiveTags);
        using (var json = JsonDocument.Parse(entry.Text))
        {
            Assert.Equal("kind:files.picker", json.RootElement.GetProperty("target").GetString());
            Assert.Equal("files", json.RootElement.GetProperty("action").GetProperty("argument").GetString());
        }
        var focused = await Eventually(runtime, presence => presence.Focus is not null);
        Assert.Equal(new Focus("kind:files.picker", FocusTone.Info, "Pick the report.", new FocusAction("open", "files")), focused.Focus);

        await Pump(runtime).Started(Agent, "run-2", null);

        await Eventually(runtime, presence => presence.Focus is null);
        Assert.Single((await ContextOf(runtime, "ui.focus").Read()).Entries);
    }

    [Fact]
    public async Task ModelCannotPointCritical()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);

        InoToolResult result;
        using (As(runtime, Who.Agent)) { result = await FocusTool(runtime).Point("window:files", FocusTone.Critical, "Look."); }

        Assert.False(result.Ok);
        Assert.Equal("Critical is reserved for the platform", result.Text);
        Assert.Empty((await ContextOf(runtime, "ui.focus").Read()).Entries);
    }

    [Fact]
    public async Task NeuronKindIsLatestPublisherOnActiveTrace()
    {
        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture);
        using var person = runtime.As(Who.Person);
        var trace = TraceContext.Begin("person", runtime.Brain.Value, new NeuronId(runtime.Brain, "edge", "request"));

        using (TraceContext.Enter(trace))
        {
            await Pump(runtime).Started(Agent, "run-1", trace.TraceId);
            await runtime.Get<IPresenceWorker>(new NeuronId(runtime.Brain, PresenceWorker.Alias, "worker")).Work();
            await Pump(runtime).Tool(Agent, "run-1", "neuron_call", "{}", 1);
        }

        var presence = await Eventually(runtime, presence => presence.NeuronKind == PresenceWorker.Alias);
        Assert.Equal(trace.TraceId, presence.TraceId);
    }

    [Fact]
    public async Task ARunningConversationTurnShowsItsTool()
    {
        var compiled = new CompiledTypes([typeof(IAgentContext).Assembly]);
        var observer = new HoldingBrainObserver(compiled.Entries);
        var client = new ToolStubChatClient();

        await using var fixture = new PresenceFixture();
        await using var runtime = await Started(fixture, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            services.AddSingleton(sp => new NeuronSurface(
                sp.GetRequiredService<INeuronProxies>(),
                observer,
                new NeuronTypeSearch(observer, [], null),
                callers: sp.GetService<ICallerAmbient>()));
            services.AddSingleton<IAiCredentials, NoProviders>();
            services.AddSingleton<ISecretRedactor, NoSecrets>();
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
        });

        using var person = runtime.As(Who.Person);
        var conversation = runtime.Get<IConversation>(new NeuronId(runtime.Brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse("main"))));
        await conversation.Seat("ino", new AgentDefinition
        {
            Instructions = "list available neurons",
            Contracts = [typeof(IAgentContext).FullName!],
        });

        await conversation.Say("list neurons");
        await observer.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        try
        {
            var working = await Eventually(runtime, presence => presence.State == PresenceState.Working && presence.Reason == "neurons_list");
            Assert.Equal(PresenceState.Working, working.State);
            Assert.Equal("neurons_list", working.Reason);
        }
        finally
        {
            observer.Release();
        }

        await Eventually(runtime, presence => presence.State == PresenceState.Idle);
    }

    private sealed class ToolStubChatClient : IChatClient
    {
        private int _step;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _step) == 1)
            {
                var call = new FunctionCallContent("c-1", "neurons_list", new Dictionary<string, object?>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _step) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c-1", "neurons_list", new Dictionary<string, object?>())]);
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class HoldingBrainObserver(BrainType[] types) : IBrainObserver
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
            => AsyncEnumerable.Empty<BrainInstance>();

        public async IAsyncEnumerable<BrainInstance> Instances(string? type, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            yield break;
        }

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(types);

        public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);

        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default) => Task.FromResult<ObservedWire[]>([]);

        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<SignalDelivery>();

        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);
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

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }
}
