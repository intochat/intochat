using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.AI.Scripted;
using DigitalBrain.Assistant;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AssistantStreamFacts
{
    [Fact]
    public async Task KnownRefusalsReachThePersonWithoutModelRewriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-refusal/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        foreach (var message in new[] { DigitalBrain.Framework.Capacity.CapacityUnavailableException.RefusalMessage })
        {
            var events = await Collect(app, new("one", Guid.NewGuid().ToString("N"), "refuse:" + message, "owner"), ct);
            Assert.Equal(message, Assert.Single(events, item => Type(item) == "RUN_ERROR").GetProperty("message").GetString());
        }
    }

    // The Assistant's model is a scripted key bound to the scenario client, so the real agent,
    // catalog and tool loop run every turn without a provider.
    private static async Task<ModuleBrain> StartBrain(CancellationToken ct) => await ModuleTest.Create().WithExecution(new TestExecutionOptions
    {
        PrivateConfiguration = new Dictionary<string, string?> { ["Assistant:Model"] = IScriptedLLM.ModelPrefix + "stream" },
    }).WithModule<AssistantModule>()
        .RequireModules([new DigitalBrain.Apps.AppsModule(), new DigitalBrain.AI.AIModule(), new DigitalBrain.Compute.ComputeModule(), new DigitalBrain.Flutter.FlutterModule()])
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<StreamScenarioClient>();
            silo.Services.AddKeyedSingleton<IChatClient>(IScriptedLLM.ModelPrefix + "stream", (services, _) => services.GetRequiredService<StreamScenarioClient>());
            silo.Services.AddSingleton<IAgentToolFactory>(new ScenarioTools());
        }).StartAsync(ct);

    [Fact]
    public async Task StreamedTextIsRetainedWithThreadAndWorkspaceIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-streamedtext/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        var events = await Collect(app, new("one", "run", "hello", "owner"), ct);
        Assert.Equal(["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED", "RECEIPT"], events.Select(Type));
        Assert.Equal("Hello world", Text(events));
        Assert.Equal("Hello world", Assert.Single((await app.ReadConversation("one", ct)).Runs).Reply);
        Assert.Equal(2, (await (await app.Conversation("one")).Transcript()).Length);
        Assert.Empty((await app.ReadConversation("two", ct)).Runs);
        Assert.Empty((await brain.Grains.GetGrain<IAssistant>(AssistantConversations.AssistantKey(key.Split("/applications/")[0] + "-other")).ReadConversation("one", ct)).Runs);
    }

    [Fact]
    public async Task ReplayIsIdempotentAndConflictingInputIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-replayisidem/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        var model = brain.SiloServices.GetRequiredService<StreamScenarioClient>();
        var request = new AssistantRun("one", "run", "replay", "owner");
        await Collect(app, request, ct);
        var before = model.Count("replay");
        var replay = await Collect(app, request, ct);
        Assert.Equal("Hello world", Text(replay));
        Assert.Equal(before, model.Count("replay"));
        Assert.DoesNotContain(replay, item => Type(item) == "RECEIPT");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(app, request with { Message = "different" }, ct));
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRun);
        Assert.Equal("replay", Assert.Single(state.Runs).UserText);
    }

    [Fact]
    public async Task UnavailableModelsDoNotAcquireAConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-unavailablem/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(app, new("one", "run", "hello", "owner", "profile:missing"), ct));
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRun);
        Assert.Empty(state.Runs);
        Assert.Contains(await Collect(app, new("one", "run", "hello", "owner"), ct), item => Type(item) == "RUN_FINISHED");
    }

    [Fact]
    public async Task FailedRunsReleaseTheConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-failedrunsre/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        var error = Assert.Single(await Collect(app, new("one", "failed", "fail", "owner"), ct), item => Type(item) == "RUN_ERROR");
        Assert.Equal("AGENT_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("failed", error.GetProperty("runId").GetString());
        Assert.DoesNotContain("scripted failure", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("data connection", error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRun);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(state.Runs).Status);
        Assert.Contains(await Collect(app, new("one", "next", "hello", "owner"), ct), item => Type(item) == "RUN_FINISHED");
    }

    [Fact]
    public async Task CancellationReleasesTheConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-cancellation/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        await using var failures = await brain.SubscribeAsync<TurnFailed>(await app.Conversation("one"), ct);
        await using var failed = failures.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var model = brain.SiloServices.GetRequiredService<StreamScenarioClient>();
        var reading = Collect(app, new("one", "cancelled", "wait", "owner"), cancel.Token);
        await model.Waiting.Task.WaitAsync(ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await model.Cancelled.Task.WaitAsync(ct);
        Assert.True(await failed.MoveNextAsync());
        Assert.True(failed.Current.Cancelled);
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRun);
        Assert.Equal(AgentRunStatus.Cancelled, Assert.Single(state.Runs).Status);
        Assert.Empty(await (await app.Conversation("one")).Transcript());
        Assert.Contains(await Collect(app, new("one", "next", "hello", "owner"), ct), item => Type(item) == "RUN_FINISHED");
    }

    [Fact]
    public async Task ConcurrentSubmissionsCannotCompleteTheOwningRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-concurrentsu/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        var model = brain.SiloServices.GetRequiredService<StreamScenarioClient>();
        var owner = Collect(app, new("one", "same", "hold", "owner"), ct);
        await model.Holding.Task.WaitAsync(ct);
        try
        {
            var rejected = await Collect(app, new("one", "same", "intruder", "owner"), ct);
            Assert.Contains(rejected, item => Type(item) == "RUN_ERROR");
            var active = await app.ReadConversation("one", ct);
            Assert.Equal("same", active.ActiveRun?.RunId);
            Assert.Empty(active.Runs);
        }
        finally { model.Release.TrySetResult(); }
        Assert.Contains(await owner, item => Type(item) == "RUN_FINISHED");
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRun);
        Assert.Equal("hold", Assert.Single(state.Runs).UserText);
        Assert.Equal(0, model.Count("intruder"));
    }

    [Fact]
    public async Task ToolEventsAndCardsTravelWithAnIntentReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-tooleventsan/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        await app.Configure(new AgentDefinition { Tools = [ScenarioTools.Table] });
        var events = await Collect(app, new("one", "tools", "tools", "owner"), ct);
        foreach (var type in new[] { "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT", "UI_CARD", "RECEIPT" })
        { Assert.Single(events, item => Type(item) == type); }
        Assert.Equal("table", events.Single(item => Type(item) == "UI_CARD").GetProperty("card").GetProperty("kind").GetString());
        var receipt = events.Single(item => Type(item) == "RECEIPT");
        Assert.Equal("Succeeded", receipt.GetProperty("outcome").GetString());
        Assert.NotEmpty(receipt.GetProperty("calls").EnumerateArray());
    }

    private static string? Type(JsonElement item) => item.GetProperty("type").GetString();
    private static string Text(IEnumerable<JsonElement> items) => string.Concat(items.Where(item => Type(item) == "TEXT_MESSAGE_CONTENT").Select(item => item.GetProperty("delta").GetString()));
    private static async Task<List<JsonElement>> Collect(IAssistant app, AssistantRun request, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        await foreach (var json in app.Run(request, ct))
        {
            using var document = JsonDocument.Parse(json);
            result.Add(document.RootElement.Clone());
        }
        return result;
    }
}

// The scenario a turn plays is named by the person's last message.
internal sealed class StreamScenarioClient : IChatClient
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Count(string message) => _calls.GetValueOrDefault(message);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) { updates.Add(update); }
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        var message = history.Last(item => item.Role == ChatRole.User).Text;
        _calls.AddOrUpdate(message, 1, (_, count) => count + 1);
        if (message == "hold")
        {
            Holding.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        if (message == "wait")
        {
            Waiting.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled.TrySetResult(); }
        }
        if (message == "fail") { throw new InvalidOperationException("scripted failure"); }
        if (message.StartsWith("refuse:", StringComparison.Ordinal)) { throw new InvalidOperationException(message[7..]); }
        if (message == "tools" && !history.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Any())
        {
            yield return new(ChatRole.Assistant, [new FunctionCallContent("call", ScenarioTools.Table, new Dictionary<string, object?>())]);
            yield break;
        }
        yield return new(ChatRole.Assistant, "Hello ");
        yield return new(ChatRole.Assistant, "world");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

// The live-table tool the "tools" scenario calls: a window result carrying a table card.
internal sealed class ScenarioTools : IAgentToolFactory
{
    public const string Table = "show_supabase_query_table";

    public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context) =>
        [AIFunctionFactory.Create(() => JsonDocument.Parse("""{"windowId":"window","title":"Customers","rowsRead":2,"_ui":{"kind":"table"}}""").RootElement.Clone(), Table)];
}
