using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class AgentTurnLoopFunctionInvocationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentTurnLoopDelegatesToolExecutionToUseFunctionInvocationAndPreservesEventOrder(bool streaming)
    {
        var toolInvokedAfterToolStartedObserved = false;
        var toolStartedObserved = false;
        var step = 0;

        var chatClient = new ScriptedTurnChatClient(
            (_, _) => step++ == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "focus_clear", new Dictionary<string, object?>()),
                ]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "All done.")));

        var probe = new ProbeFocusNeuron(() =>
        {
            toolInvokedAfterToolStartedObserved = toolStartedObserved;
            return new InoToolResult("cleared-probe");
        });

        await using var harness = CreateLoopHarness(chatClient, probe);
        var plan = CreatePlan([typeof(IFocus).FullName!], maxModelCalls: 4, streaming: streaming);

        var events = new List<AgentTurnEvent>();
        await foreach (var ev in harness.Loop.Run(plan, Ct))
        {
            if (ev is AgentTurnEvent.ToolStarted)
            {
                toolStartedObserved = true;
            }

            events.Add(ev);
        }

        Assert.True(toolInvokedAfterToolStartedObserved, "ToolStarted must be yielded before the tool executes.");
        Assert.IsType<AgentTurnEvent.Started>(events[0]);
        Assert.IsType<AgentTurnEvent.ModelSelected>(events[1]);
        var started = Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>());
        Assert.Equal(("call-1", "focus_clear"), (started.CallId, started.Name));
        var completed = Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>());
        Assert.Equal("call-1", completed.CallId);
        Assert.Contains("cleared-probe", completed.Result, StringComparison.Ordinal);
        Assert.Contains(events.OfType<AgentTurnEvent.TextChunk>(), static c => c.Content.Contains("All done.", StringComparison.Ordinal));
        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.IsType<AgentTurnEvent.Finished>(events[^1]);
    }

    [Fact]
    public async Task AgentTurnLoopRefusesUnknownToolsWithToolUnavailablePayload()
    {
        var step = 0;
        var chatClient = new ScriptedTurnChatClient(
            (_, _) => step++ == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-missing", "nonexistent_tool", new Dictionary<string, object?>()),
                ]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered.")));

        await using var harness = CreateLoopHarness(chatClient, new ProbeFocusNeuron(() => new InoToolResult("ok")));
        var plan = CreatePlan([typeof(IFocus).FullName!], maxModelCalls: 4, streaming: false);

        var events = new List<AgentTurnEvent>();
        await foreach (var ev in harness.Loop.Run(plan, Ct))
        {
            events.Add(ev);
        }

        var started = Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>());
        Assert.Equal("nonexistent_tool", started.Name);
        var completed = Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>());
        Assert.Contains("tool_unavailable", completed.Result, StringComparison.Ordinal);
        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.IsType<AgentTurnEvent.Finished>(events[^1]);
    }

    [Fact]
    public async Task AgentTurnLoopEmitsToolFailedAndTurnFailedImmediatelyWhenToolThrows()
    {
        var calls = 0;
        var chatClient = new ScriptedTurnChatClient((_, _) =>
        {
            calls++;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-boom", "focus_clear", new Dictionary<string, object?>()),
            ]));
        });

        // Running without a stamped caller causes AgentTools.Marked to throw out of ContractTool.InvokeAsync,
        // exercising AgentTurnLoop's ToolFailed + immediate termination (MaximumConsecutiveErrorsPerRequest = 0).
        await using var harness = CreateLoopHarness(chatClient, new ProbeFocusNeuron(() => new InoToolResult("ok")), enterPersonScope: false);
        var plan = CreatePlan([typeof(IFocus).FullName!], maxModelCalls: 4, streaming: false);

        var events = new List<AgentTurnEvent>();
        await foreach (var ev in harness.Loop.Run(plan, Ct))
        {
            events.Add(ev);
        }

        Assert.Equal(1, calls);
        var toolFailed = Assert.Single(events.OfType<AgentTurnEvent.ToolFailed>());
        Assert.Equal(("call-boom", "focus_clear", "tool_failed"), (toolFailed.CallId, toolFailed.Name, toolFailed.Code));
        var turnFailed = Assert.IsType<AgentTurnEvent.TurnFailed>(events[^1]);
        Assert.Contains("stamped caller", turnFailed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentTurnLoopEnforcesMaxModelCallsBudget()
    {
        var callCounter = 0;
        var chatClient = new ScriptedTurnChatClient((_, _) =>
        {
            var id = $"call-{++callCounter}";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(id, "focus_clear", new Dictionary<string, object?>()),
            ]));
        });

        await using var harness = CreateLoopHarness(chatClient, new ProbeFocusNeuron(() => new InoToolResult("ok")));
        var plan = CreatePlan([typeof(IFocus).FullName!], maxModelCalls: 2, streaming: false);

        var events = new List<AgentTurnEvent>();
        await foreach (var ev in harness.Loop.Run(plan, Ct))
        {
            events.Add(ev);
        }

        Assert.Equal(2, callCounter);
        var turnFailed = Assert.IsType<AgentTurnEvent.TurnFailed>(events[^1]);
        Assert.Contains("model-call budget", turnFailed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelCatalogResolveRoleClientAsyncRejectsHostWithoutChatClientAndMapsCanonicalSelections()
    {
        var hostModel = new ResolvedModel(AiProvider.Host, "default", Endpoint: null, Profile: null, Revision: "host", Abilities.Text | Abilities.Tools, Reasoning: null, MaxOutputTokens: null);
        var profileModel = new ResolvedModel(AiProvider.Ollama, "qwen2.5-coder:14b", Endpoint: null, Profile: "local-coder", Revision: "local", Abilities.Text | Abilities.Tools, Reasoning: "medium", MaxOutputTokens: 2048);
        var ollamaModel = new ResolvedModel(AiProvider.Ollama, "qwen2.5-coder:14b", Endpoint: null, Profile: null, Revision: "local", Abilities.Text | Abilities.Tools, Reasoning: null, MaxOutputTokens: null);
        var cloudModel = new ResolvedModel(AiProvider.Anthropic, "claude-sonnet-4-6", Endpoint: null, Profile: null, Revision: "cloud", Abilities.Text | Abilities.Tools, Reasoning: null, MaxOutputTokens: null);

        Assert.Null(ModelCatalog.ToSelection(hostModel));
        Assert.Equal(new ModelSelection("profile:local-coder", "medium", 2048), ModelCatalog.ToSelection(profileModel));
        Assert.Equal(new ModelSelection("ollama/qwen2.5-coder:14b"), ModelCatalog.ToSelection(ollamaModel));
        Assert.Equal(new ModelSelection("claude-sonnet-4-6"), ModelCatalog.ToSelection(cloudModel));

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<ModelCatalog>();
        await using var sp = services.BuildServiceProvider();

        var catalog = sp.GetRequiredService<ModelCatalog>();
        await Assert.ThrowsAsync<NoProviderConfiguredException>(() =>
            catalog.ResolveRoleClientAsync(brain: null, ModelRole.Think, requiresTools: true, ct: Ct));
    }

    private static TurnPlan CreatePlan(string[] contracts, int maxModelCalls, bool streaming) => new(
        ScopeId: "ada",
        Conversation: "ada/ino/conversations/c1",
        RunId: "run-1",
        Transcript: [],
        Summary: null,
        Input: new AiMessage("user", [new AiText("ping")]),
        Instructions: "Be concise.",
        Contracts: contracts,
        Contexts: [],
        Model: null,
        Options: null,
        MaxModelCalls: maxModelCalls,
        Streaming: streaming);

    private static LoopHarness CreateLoopHarness(
        IChatClient chatClient,
        ProbeFocusNeuron probe,
        bool enterPersonScope = true)
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton(chatClient);
        services.AddSingleton(probe.Handler);

        TestOS? osRef = null;
        var compiled = new CompiledTypes([typeof(IFocus).Assembly]);
        var observer = new StubObserver(compiled.Entries);
        services.AddSingleton<NeuronSurface>(sp => new NeuronSurface(osRef!.Proxies, observer, new NeuronTypeSearch(observer, [], null), callers: new EdgeCallerAmbient(osRef!.Edge)));
        services.AddSingleton(sp => osRef!.Edge);
        services.AddSingleton(sp => osRef!.Proxies);
        var sp = services.BuildServiceProvider();

        var os = TestOS.Local(o => o.Services = sp);
        osRef = os;
        os.Register<ProbeFocusNeuron>();

        var personScope = enterPersonScope ? os.Edge.Enter(os.Edge.Person("ada", "ada", new BrainId("ada"))) : null;
        return new LoopHarness(os, sp, personScope, new AgentTurnLoop(sp));
    }

    private sealed class LoopHarness(TestOS os, ServiceProvider sp, IDisposable? scope, AgentTurnLoop loop) : IAsyncDisposable
    {
        public AgentTurnLoop Loop => loop;

        public async ValueTask DisposeAsync()
        {
            scope?.Dispose();
            await sp.DisposeAsync();
            await os.DisposeAsync();
        }
    }

    [Neuron("ino.focus")]
    private sealed class ProbeFocusNeuron(Func<InoToolResult> onClear) : Neuron, IFocus
    {
        public Func<InoToolResult> Handler => onClear;
        public Task<InoToolResult> Point(string target, FocusTone tone, string reason, FocusAction? action = null)
            => Task.FromResult(new InoToolResult(target));
        public Task<InoToolResult> Show(string target, string reason)
            => Task.FromResult(new InoToolResult(target));
        public Task<InoToolResult> Clear() => Task.FromResult(onClear());
    }

    private sealed class ScriptedTurnChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse> onCall) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(onCall(messages, options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = onCall(messages, options);
            await Task.Yield();
            foreach (var msg in response.Messages)
            {
                yield return new ChatResponseUpdate(msg.Role, msg.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EdgeCallerAmbient(EdgeAuthority edge) : ICallerAmbient
    {
        public Caller? Current => edge.Current;
    }

    private sealed class StubObserver(BrainType[] types) : IBrainObserver
    {
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
            => AsyncEnumerable.Empty<BrainInstance>();

        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<BrainInstance>();

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(types);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);
        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default) => Task.FromResult<ObservedWire[]>([]);
        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<SignalDelivery>();
        public Task<System.Text.Json.JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default) => Task.FromResult<System.Text.Json.JsonElement?>(null);
    }
}
