using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.Assistant;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.FileInput;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Module;
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

    private static async Task<ModuleBrain> StartBrain(CancellationToken ct) => await ModuleTest.Create().WithExecution(new TestExecutionOptions
    {
        PrivateConfiguration = new Dictionary<string, string?> { [IntegrationSchema.Key("openai", "ApiKey")] = "test-no-network" },
    }).WithModule<AssistantModule>()
        .RequireModules([new DigitalBrain.Apps.AppsModule(), new DigitalBrain.AI.AIModule(), new DigitalBrain.Compute.ComputeModule(), new DigitalBrain.Flutter.FlutterModule()])
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<StreamScenarioRunner>();
            silo.Services.AddSingleton<IAgentTurnRunner>(services => services.GetRequiredService<StreamScenarioRunner>());
            silo.Services.Configure<AIOptions>(options =>
            {
                options.ModelProfiles["default"] = new() { Provider = "OpenAI", Model = "gpt-4.1-mini", Capabilities = LlmCapabilities.Tools };
                options.Default.Model = "default";
            });
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
        Assert.Equal("Hello world", Assert.Single((await app.ReadConversation("one", ct)).Turns).AssistantText);
        Assert.Empty((await app.ReadConversation("two", ct)).Turns);
        Assert.Empty((await brain.Grains.GetGrain<IAssistant>(AssistantConversations.AssistantKey(key.Split("/applications/")[0] + "-other")).ReadConversation("one", ct)).Turns);
    }

    [Fact]
    public async Task ReplayIsIdempotentAndConflictingInputIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-replayisidem/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        var runner = brain.SiloServices.GetRequiredService<StreamScenarioRunner>();
        var request = new AssistantRun("one", "run", "replay", "owner");
        await Collect(app, request, ct);
        var before = runner.Count("replay");
        var replay = await Collect(app, request, ct);
        Assert.Equal("Hello world", Text(replay));
        Assert.Equal(before, runner.Count("replay"));
        Assert.DoesNotContain(replay, item => Type(item) == "RECEIPT");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(app, request with { Message = "different" }, ct));
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRunId);
        Assert.Equal("replay", Assert.Single(state.Turns).UserText);
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
        Assert.Null(state.ActiveRunId);
        Assert.Empty(state.Turns);
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
        Assert.Null((await app.ReadConversation("one", ct)).ActiveRunId);
        Assert.Contains(await Collect(app, new("one", "next", "hello", "owner"), ct), item => Type(item) == "RUN_FINISHED");
    }

    [Fact]
    public async Task CancellationReleasesTheConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-cancellation/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runner = brain.SiloServices.GetRequiredService<StreamScenarioRunner>();
        var reading = Collect(app, new("one", "cancelled", "wait", "owner"), cancel.Token);
        await runner.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await runner.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await app.ReadConversation("one", ct)).ActiveRunId is not null && DateTime.UtcNow < deadline)
        { await Task.Delay(20, ct); }
        Assert.Null((await app.ReadConversation("one", ct)).ActiveRunId);
        Assert.Empty((await app.ReadConversation("one", ct)).Turns);
        Assert.Contains(await Collect(app, new("one", "next", "hello", "owner"), ct), item => Type(item) == "RUN_FINISHED");
    }

    [Fact]
    public async Task ConcurrentSubmissionsCannotCompleteTheOwningRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-concurrentsu/applications/assistant";
        var app = brain.Get<IAssistant>(key);
        var runner = brain.SiloServices.GetRequiredService<StreamScenarioRunner>();
        var owner = Collect(app, new("one", "same", "hold", "owner"), ct);
        await runner.Holding.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        try
        {
            var rejected = await Collect(app, new("one", "same", "intruder", "owner"), ct);
            Assert.Contains(rejected, item => Type(item) == "RUN_ERROR");
            var active = await app.ReadConversation("one", ct);
            Assert.Equal("same", active.ActiveRunId);
            Assert.Empty(active.Turns);
        }
        finally { runner.Release.TrySetResult(); }
        Assert.Contains(await owner, item => Type(item) == "RUN_FINISHED");
        var state = await app.ReadConversation("one", ct);
        Assert.Null(state.ActiveRunId);
        Assert.Equal("hold", Assert.Single(state.Turns).UserText);
        Assert.Equal(0, runner.Count("intruder"));
    }

    [Fact]
    public async Task ToolEventsAndCardsTravelWithAnIntentReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartBrain(ct);
        const string key = "stream-tooleventsan/applications/assistant";
        var events = await Collect(brain.Get<IAssistant>(key), new("one", "tools", "tools", "owner"), ct);
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

internal sealed class StreamScenarioRunner : IAgentTurnRunner
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource UiHolding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Count(string message) => _calls.GetValueOrDefault(message);
    public async IAsyncEnumerable<AgentTurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        _calls.AddOrUpdate(request.Message, 1, (_, count) => count + 1);
        if (request.Message == "ui-wait")
        {
            UiHolding.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        if (request.Message == "hold")
        {
            Holding.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
        if (request.Message == "wait")
        {
            Waiting.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Cancelled.TrySetResult(); }
        }
        if (request.Message == "fail") { yield return new AgentTurnEvent.Failed("scripted failure"); yield break; }
        if (request.Message.StartsWith("refuse:", StringComparison.Ordinal)) { yield return new AgentTurnEvent.Failed(request.Message[7..]); yield break; }
        if (request.Message == "long-answer")
        {
            yield return new AgentTurnEvent.Text(new string('a', 32_000));
            yield return new AgentTurnEvent.Text(new string('b', 8_123));
            yield return new AgentTurnEvent.Finished();
            yield break;
        }
        if (request.Message == "window-handles")
        {
            yield return new AgentTurnEvent.ToolStarted("table-call", "show_supabase_query_table", "{}");
            yield return new AgentTurnEvent.ToolCompleted("table-call", "show_supabase_query_table", "{\"windowId\":\"table-result\",\"title\":\"Customers\",\"kind\":\"table\"}");
            yield return new AgentTurnEvent.ToolStarted("form-call", "show_form", "{}");
            yield return new AgentTurnEvent.ToolCompleted("form-call", "show_form", "{\"windowId\":\"form-result\",\"title\":\"Details\",\"formId\":\"form-neuron\"}");
        }
        if (request.Message == "tools")
        {
            yield return new AgentTurnEvent.ToolStarted("call", "show_supabase_query_table", "{}");
            yield return new AgentTurnEvent.ToolCompleted("call", "show_supabase_query_table", "{\"windowId\":\"window\",\"title\":\"Customers\",\"rowsRead\":2,\"_ui\":{\"kind\":\"table\"}}");
        }
        if (request.Message == "card-actions")
        {
            yield return new AgentTurnEvent.ToolStarted("reference", "reference", "{}");
            yield return new AgentTurnEvent.ToolCompleted("reference", "reference", """
                {"_ui":{"title":"Reference","actions":[
                  {"label":"Docs","url":"https://example.com"},
                  {"label":"Open table","id":"table-result","windowId":"table-result","remoteManaged":true,"kind":"table"}
                ]}}
                """);
        }
        yield return new AgentTurnEvent.Text("Hello ");
        yield return new AgentTurnEvent.Text("world");
        yield return new AgentTurnEvent.Finished();
    }
}
