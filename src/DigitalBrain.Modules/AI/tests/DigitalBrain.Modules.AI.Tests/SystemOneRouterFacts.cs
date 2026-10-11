using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Routing;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.AI.SystemOne;
using DigitalBrain.AI.SystemOne.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Time.Timers;
using ITimer = DigitalBrain.Time.Timers.ITimer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

// The Jev router reads the deployed contract catalog, picks a capability with a System One model,
// and fills the call with a language model. Here it routes "set a timer" to ITimer.Start.
public sealed class SystemOneRouterFacts
{
    [Fact]
    public async Task DispatchRoutesToTheTimerAndStartsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Contract = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"timer","confidence":0.95,"probabilities":{"timer":0.95}}}}""";
        const string Method = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Start","confidence":0.95,"probabilities":{"Start":0.95}}}}""";
        var replies = new[]
        {
            server.ReplyOnce("application/json", Contract, ct),
            server.ReplyOnce("application/json", Method, ct),
            server.ReplyOnce("application/json", Contract, ct),
            server.ReplyOnce("application/json", Method, ct),
        };
        var scope = "";
        var model = Answering(() => $$"""{"key":"{{scope}}/timer","arguments":["00:05:00",null]}""");
        await using var host = await Start(server.Url, model, ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        scope = alice.BrainId;

        var outcome = await host.Get<IRouter>(scope).Dispatch("set a timer for 5 minutes", [typeof(ITimer).FullName!], ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        Assert.Equal(typeof(ITimer).FullName, outcome.Route.Contract);
        Assert.Equal("Start", outcome.Route.Method);
        Assert.Equal(scope + "/timer", outcome.Key);
        Assert.Contains("\"timer\"", (await replies[0]).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAnswersTheChosenContractAndMethod()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Contract = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"timer","confidence":0.91,"probabilities":{"timer":0.91}}}}""";
        const string Method = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Start","confidence":0.88,"probabilities":{"Start":0.88}}}}""";
        var replies = new[] { server.ReplyOnce("application/json", Contract, ct), server.ReplyOnce("application/json", Method, ct) };
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;

        var route = await host.Get<IRouter>(scope).Route("start a countdown please", [typeof(ITimer).FullName!], ct);

        Assert.Equal(typeof(ITimer).FullName, route.Contract);
        Assert.Equal("Start", route.Method);
        Assert.Equal(0.91, route.Confidence);
        await Task.WhenAll(replies);
    }

    [Fact]
    public async Task RouteChoosesARuntimeToolOnItsKeyWithoutAMethodQuestion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct,
            services => services.AddSingleton<IRuntimeCapabilitySource>(new OneTool()));
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        var tool = $"tool:{scope}/mcp/photocraft/render_preview";
        var choice = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        { ["capability_0"] = new { type = "choice", choice = tool, confidence = 0.93, probabilities = new Dictionary<string, double> { [tool] = 0.93 } } });
        var reply = server.ReplyOnce("application/json", "{\"model\":\"jev\",\"answers\":" + choice + "}", ct);

        var route = await host.Get<IRouter>(scope).Route("show me the image", null, ct);

        Assert.Equal(("DigitalBrain.Mcp.IMcpServer", "Run", scope + "/mcp/photocraft", "render_preview"), (route.Contract, route.Method, route.Key, route.Tool));
        Assert.Contains("Render the document", (await reply).Body, StringComparison.Ordinal);
    }

    // One runtime tool in every brain: an MCP server's tool stands in without an MCP server.
    private sealed class OneTool : IRuntimeCapabilitySource
    {
        public Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NeuronType>>(
                [new($"tool:{brainId}/mcp/photocraft/render_preview", "tools", "PhotoCraft: Render preview", "Render the document as a PNG.",
                    ["Task<ToolResult> Run(\"render_preview\", arguments)"], "DigitalBrain.Mcp.IMcpServer", brainId + "/mcp/photocraft", "render_preview")]);
    }

    [Fact]
    public async Task OpenCalculatorRoutesToTheAppWithoutAnyModelCall()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string App = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.open","confidence":0.95,"probabilities":{"calculator.open":0.95,"something else":0.05}}}}""";
        var reply = server.ReplyOnce("application/json", App, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        var app = host.Get<IApp>(IApp.Key(scope, "calculator"));
        await app.Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        await using var requested = await host.SubscribeAsync<AppRequested>(app, ct);
        await using var signals = requested.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        var outcome = await host.Get<IRouter>(scope).Dispatch("open calculator", null, ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        Assert.Equal(typeof(IApp).FullName, outcome.Route.Contract);
        Assert.Equal("Request", outcome.Route.Method);
        Assert.Equal(("calculator", "open"), (outcome.Route.App, outcome.Route.Operation));
        Assert.True(outcome.Route.IsDirect);
        Assert.Null(outcome.Arguments);
        Assert.True(await signals.MoveNextAsync());
        Assert.Equal("open", signals.Current.Operation);
        Assert.Equal("open calculator", signals.Current.Input);
        await reply;
    }

    [Fact]
    public async Task SomethingElseFallsThroughToTheContractsTheCallerNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string AppElse = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"something else","confidence":0.6,"probabilities":{"something else":0.6,"calculator.open":0.4}}}}""";
        const string Contract = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"timer","confidence":0.91,"probabilities":{"timer":0.91}}}}""";
        const string Method = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Start","confidence":0.88,"probabilities":{"Start":0.88}}}}""";
        var replies = new[]
        {
            server.ReplyOnce("application/json", AppElse, ct),
            server.ReplyOnce("application/json", Contract, ct),
            server.ReplyOnce("application/json", Method, ct),
        };
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");

        var route = await host.Get<IRouter>(scope).Route("open calculator", [typeof(ITimer).FullName!], ct);

        Assert.Null(route.App);
        Assert.Equal(typeof(DigitalBrain.Time.Timers.ITimer).FullName, route.Contract);
        Assert.Equal("Start", route.Method);
        await Task.WhenAll(replies);
    }

    [Fact]
    public async Task ARequestNoAppTakesIsNotRoutedOverTheDeployedCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string AppElse = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"something else","confidence":0.9,"probabilities":{"something else":0.9,"calculator.open":0.1}}}}""";
        var reply = server.ReplyOnce("application/json", AppElse, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<IRouter>(scope).Route("start a countdown please", null, ct));
        await reply;
    }

    [Fact]
    public async Task AchieveChainsCapabilitiesUntilTheGoalIsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string PickText = """{"model":"jev","answers":{"next":{"type":"choice","choice":"text","confidence":0.95,"probabilities":{"text":0.95}}}}""";
        const string PickSet = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Set","confidence":0.95,"probabilities":{"Set":0.95}}}}""";
        const string Done = """{"model":"jev","answers":{"next":{"type":"choice","choice":"finish","confidence":0.95,"probabilities":{"finish":0.95}}}}""";
        var replies = new[]
        {
            server.ReplyOnce("application/json", PickText, ct),
            server.ReplyOnce("application/json", PickSet, ct),
            server.ReplyOnce("application/json", Done, ct),
        };
        var scope = "";
        var model = Answering(() => $$"""{"key":"{{scope}}/notepad","arguments":["hello from Jev"]}""");
        await using var host = await Start(server.Url, model, ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        scope = alice.BrainId;

        var run = await host.Get<IRouter>(scope).Achieve("write a note for me", [typeof(IText).FullName!], ct);

        Assert.Single(run.Steps);
        Assert.Equal("Set", run.Steps[0].Method);
        Assert.Equal("hello from Jev", (await host.Get<IText>(scope + "/notepad").Read()).Markdown);
        await Task.WhenAll(replies);
    }

    [Fact]
    public async Task AnAskedRouteIsAnsweredAsStateAndARoutedSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Contract = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"timer","confidence":0.91,"probabilities":{"timer":0.91}}}}""";
        const string Method = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Start","confidence":0.88,"probabilities":{"Start":0.88}}}}""";
        var replies = new[] { server.ReplyOnce("application/json", Contract, ct), server.ReplyOnce("application/json", Method, ct) };
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        var router = host.Get<IRouter>(scope);
        await using var routed = await host.Observe<Routed>(router, ct);

        var id = Guid.NewGuid();

        await router.Ask(new RouteRequest(id, "start a countdown please", [typeof(ITimer).FullName!]), ct);

        var signal = await routed.NextAsync(ct: ct);
        Assert.Equal(id, signal.Id);
        Assert.Equal(typeof(ITimer).FullName, signal.Contract);
        Assert.Equal("Start", signal.Method);
        Assert.Equal(0.91, signal.Confidence);
        var pending = await router.Pending(id, ct);
        Assert.Equal("Routed", pending?.Status);
        Assert.Equal("Start", pending?.Result?.Method);
        await Task.WhenAll(replies);
    }

    [Fact]
    public async Task ASystemOneAnswerIsPublishedAsADecision()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var reply = server.ReplyOnce("application/json",
            """{"model":"jev","answers":{"department":{"type":"choice","choice":"technical","confidence":0.78,"probabilities":{"technical":0.85,"billing":0.15}}}}""", ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        var systemOne = host.Get<ISystemOne>(scope);
        await using var decided = await host.Observe<SystemOneDecided>(systemOne, ct);

        await systemOne.Ask(new SystemOneRequest("the integration keeps failing",
            new() { ["department"] = new SystemOneQuestion.Choice("Which team?", new() { ["technical"] = null, ["billing"] = null }) }), ct);

        var signal = await decided.NextAsync(ct: ct);
        Assert.Equal("the integration keeps failing", signal.RequestSummary);
        Assert.Equal(0.78, signal.Confidence);
        Assert.Equal("technical", Assert.IsType<SystemOneAnswer.Choice>(signal.Answers["department"]).Value);
        await reply;
    }

    // Jev is the loopback server; the language model that fills calls is the script behind the real adapter.
    private static Task<ITestBrain> Start(string endpoint, IChatClient model, CancellationToken ct, Action<IServiceCollection>? services = null)
    {
        var brain = TestBrain.Create()
            .WithIntegration(AIModule.TypeSafeIntegration, "ApiKey", "test-key")
            .WithIntegration(AIModule.TypeSafeIntegration, "Endpoint", endpoint)
            .WithModule<AIModule>()
            .WithModule<DigitalBrain.Apps.AppsModule>()
            .WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithModule<DigitalBrain.Time.TimeModule>()
            .WithChatModel(model);
        return (services is null ? brain : brain.ConfigureServices(services)).StartAsync(ct);
    }

    private static StubChatClient Answering(Func<string> answer)
        => new((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer()))));
}
