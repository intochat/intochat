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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using ITimer = DigitalBrain.Time.Timers.ITimer;

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
        // "open" takes what the shell posts to open an app, never the sentence that asked for it.
        Assert.Equal(("open", ""), (signals.Current.Operation, signals.Current.Input));
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

        var route = await host.Get<IRouter>(scope).Route("open calculator", [typeof(IApp).FullName!, typeof(ITimer).FullName!], ct);

        Assert.Null(route.App);
        Assert.Equal(typeof(DigitalBrain.Time.Timers.ITimer).FullName, route.Contract);
        Assert.Equal("Start", route.Method);
        // The app tier is how apps are routed: the second tier never offers the raw app contract.
        Assert.DoesNotContain("apps.app2", (await replies[1]).Body, StringComparison.Ordinal);
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

        var route = await host.Get<IRouter>(scope).Route("start a countdown please", null, ct);

        Assert.Equal(("", "", 0.0), (route.Contract, route.Method, route.Confidence));
        await reply;
    }

    // Seen live from the local adapter ("what's the weather in Kyiv"): a choice its own probabilities rank
    // below "something else", with a confidence that belongs to neither. The distribution decides.
    [Fact]
    public async Task AChoiceItsOwnProbabilitiesContradictFollowsTheProbabilities()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string App = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.open","confidence":0.8,"probabilities":{"something else":0.6,"calculator.open":0.4}}}}""";
        const string Contract = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"timer","confidence":0.91,"probabilities":{"timer":0.91}}}}""";
        const string Method = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Start","confidence":0.88,"probabilities":{"Start":0.88}}}}""";
        var replies = new[]
        {
            server.ReplyOnce("application/json", App, ct),
            server.ReplyOnce("application/json", Contract, ct),
            server.ReplyOnce("application/json", Method, ct),
        };
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");

        var route = await host.Get<IRouter>(scope).Route("what's the weather in Kyiv", [typeof(IApp).FullName!, typeof(ITimer).FullName!], ct);

        Assert.Null(route.App);
        Assert.Equal((typeof(ITimer).FullName, "Start", 0.91), (route.Contract, route.Method, route.Confidence));
        await Task.WhenAll(replies);
    }

    [Fact]
    public async Task TheRouteConfidenceIsTheChosenOptionsOwnProbability()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string App = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.open","confidence":0.95,"probabilities":{"calculator.open":0.7,"something else":0.3}}}}""";
        var reply = server.ReplyOnce("application/json", App, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");

        var route = await host.Get<IRouter>(scope).Route("open calculator", null, ct);

        Assert.Equal(("calculator", "open", 0.7), (route.App, route.Operation, route.Confidence));
        await reply;
    }

    // Seen live: the app tier answers "something else", and the second tier's answer names no Value although
    // its distribution names the brain's runtime tool. Route used to throw "did not choose a capability".
    [Fact]
    public async Task SomethingElseReachesTheRuntimeToolTheSecondTierRanksHighest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct,
            services => services.AddSingleton<IRuntimeCapabilitySource>(new OneTool()));
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        var tool = $"tool:{scope}/mcp/photocraft/render_preview";
        const string AppElse = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"something else","confidence":0.9,"probabilities":{"something else":0.9,"calculator.open":0.1}}}}""";
        var unnamed = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        { ["capability_0"] = new { type = "choice", choice = "", confidence = 0.0, probabilities = new Dictionary<string, double> { [tool] = 0.85 } } });
        var replies = new[]
        {
            server.ReplyOnce("application/json", AppElse, ct),
            server.ReplyOnce("application/json", "{\"model\":\"jev\",\"answers\":" + unnamed + "}", ct),
        };

        var route = await host.Get<IRouter>(scope).Route("make a 512x512 image with a blue-to-orange gradient", null, ct);

        Assert.Equal(("DigitalBrain.Mcp.IMcpServer", "Run", scope + "/mcp/photocraft", "render_preview", 0.85), (route.Contract, route.Method, route.Key, route.Tool, route.Confidence));
        await Task.WhenAll(replies);
    }

    // Seen live ("translate hello to French"): the local adapter chose an option the menu never offered.
    // Nothing offered is named, so the route is unclear and the request goes to the agent turn.
    [Fact]
    public async Task AnAnswerNamingNothingOfferedIsAnUnclearRouteNotAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string AppElse = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"something else","confidence":0.9,"probabilities":{"something else":0.9,"calculator.open":0.1}}}}""";
        const string Invented = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"assistant.translate","confidence":0.9,"probabilities":{"assistant.translate":0.8,"assistant.send":0.2}}}}""";
        var replies = new[] { server.ReplyOnce("application/json", AppElse, ct), server.ReplyOnce("application/json", Invented, ct) };
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");

        var outcome = await host.Get<IRouter>(scope).Dispatch("translate hello to French", [typeof(IApp).FullName!, typeof(ITimer).FullName!], ct);

        Assert.False(outcome.Dispatched);
        Assert.Equal(("", "", 0.0), (outcome.Route.Contract, outcome.Route.Method, outcome.Route.Confidence));
        Assert.Equal("The request was not clear enough to route.", outcome.Error);
        await Task.WhenAll(replies);
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

    // Seen live: a capability id outside the menu threw "Sequence contains no matching element" and the asked
    // route failed. Nothing offered was named, so the request is decided with no route: the answer carries no
    // result whose hint would print "- . (0.00)", and says why.
    [Fact]
    public async Task ACapabilityOutsideTheMenuAnswersAnAskedRouteWithNoRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Invented = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"assistant.translate","confidence":0.9,"probabilities":{"assistant.translate":0.9}}}}""";
        var reply = server.ReplyOnce("application/json", Invented, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var router = host.Get<IRouter>(alice.BrainId);
        await using var routed = await host.Observe<Routed>(router, ct);
        var id = Guid.NewGuid();

        await router.Ask(new RouteRequest(id, "translate hello to French", [typeof(ITimer).FullName!]), ct);

        var signal = await routed.NextAsync(ct: ct);
        Assert.Equal((id, "", "", 0.0, NotClear), (signal.Id, signal.Contract, signal.Method, signal.Confidence, signal.Error));
        var pending = await router.Pending(id, ct);
        Assert.Equal(("Routed", NotClear), (pending?.Status, pending?.Error));
        Assert.Null(pending?.Result);
        await reply;
    }

    // Seen live: the app tier ran whatever the caller allowed, so a caller naming only the timer could be
    // answered with an app operation it never offered.
    [Fact]
    public async Task TheAppTierIsSkippedWhenTheCallerAllowsNoApps()
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
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(Calculator(), "spec");

        var route = await host.Get<IRouter>(scope).Route("start a countdown please", [typeof(ITimer).FullName!], ct);

        Assert.Equal((typeof(ITimer).FullName, "Start", null), (route.Contract, route.Method, route.App));
        Assert.DoesNotContain("calculator.open", (await replies[0]).Body, StringComparison.Ordinal);
        await Task.WhenAll(replies);
    }

    // Seen live ("hello" sent to the assistant): Jev was offered the assistant's own operations and routed the
    // message back to assistant.open. The asking app, known from the stamped caller, is never offered to itself.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheAskingAppIsNeverOfferedItsOwnOperations(bool migrated)
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Back = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"assistant.open","confidence":0.9,"probabilities":{"assistant.open":0.9,"something else":0.1}}}}""";
        var reply = server.ReplyOnce("application/json", Back, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct, services => services.RemoveAll<IAppRuntime>());
        var alice = await host.PersonAsync("alice", ct);
        var scope = alice.BrainId;
        var router = host.Get<IRouter>(scope);
        SignalProbe<Routed> routed;
        using (alice.AsCaller())
        {
            await host.Get<IApp>(IApp.Key(scope, "assistant")).Create(new AppManifest("Assistant", "talk to your brain",
                [new("open", "compose the assistant surface"), new("send", "start a turn with this message")], [], []), "spec");
            if (migrated)
            {
                await host.Get<IApp>(IApp.Key(scope, "ino")).Create(new AppManifest("Ino", "talk to your brain",
                    [new("open", "compose the assistant surface"), new("send", "start a turn with this message")], [], []), "spec");
                var grains = host.Services.GetRequiredService<IGrainFactory>();
                var old = grains.GetGrain<IAppMigration>(IApp.Key(scope, "assistant"));
                await old.Pause("test");
                await grains.GetGrain<IAppMigration>(IApp.Key(scope, "ino")).Pause("test");
                await old.Redirect("test", IApp.Key(scope, "ino"));
            }
            await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(Calculator(), "spec");
            routed = await host.Observe<Routed>(router, ct);
        }
        await using var observed = routed;
        using var assistant = alice.AsApp(IApp.Key(scope, migrated ? "ino" : "assistant"));
        var id = Guid.NewGuid();

        await router.Ask(new RouteRequest(id, "hello"), ct);

        var signal = await routed.NextAsync(ct: ct);
        Assert.Equal((id, "", NotClear), (signal.Id, signal.Contract, signal.Error));
        var asked = (await reply).Body;
        Assert.DoesNotContain("assistant.", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("ino.", asked, StringComparison.Ordinal);
        Assert.Contains("calculator.open", asked, StringComparison.Ordinal);
    }

    // Seen live: Dispatch decided the request again, so the route it carried out could differ from the route
    // the caller was shown. A route the router already decided for the request is the one carried out.
    [Fact]
    public async Task DispatchCarriesOutTheRouteAlreadyDecidedForTheRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Open = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.open","confidence":0.95,"probabilities":{"calculator.open":0.95,"something else":0.05}}}}""";
        const string Clear = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.clear","confidence":0.95,"probabilities":{"calculator.clear":0.95,"something else":0.05}}}}""";
        var decided = server.ReplyOnce("application/json", Open, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        var app = host.Get<IApp>(IApp.Key(scope, "calculator"));
        await app.Create(Calculator(), "spec");
        var router = host.Get<IRouter>(scope);
        await using var routed = await host.Observe<Routed>(router, ct);
        await using var requested = await host.Observe<AppRequested>(app, ct);
        var id = Guid.NewGuid();
        await router.Ask(new RouteRequest(id, "open calculator"), ct);
        Assert.Equal(id, (await routed.NextAsync(ct: ct)).Id);
        Assert.Equal(("calculator", "open"), ((await router.Pending(id, ct))?.Result?.App, (await router.Pending(id, ct))?.Result?.Operation));
        var again = server.ReplyOnce("application/json", Clear, ct);

        var outcome = await router.Dispatch("open calculator", null, ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        Assert.Equal(("calculator", "open"), (outcome.Route.App, outcome.Route.Operation));
        Assert.Equal("open", (await requested.NextAsync(ct: ct)).Operation);
        Assert.False(again.IsCompleted);
        await decided;
    }

    // Seen live ("add 17+25 with the calculator"): the app tier posted the whole sentence as the operation's
    // input. An operation whose input has to be filled from the request is left to the agent turn.
    [Fact]
    public async Task AnAppOperationThatNeedsInputIsNotDispatchedWithTheWholeRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Add = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.add","confidence":0.95,"probabilities":{"calculator.add":0.95,"something else":0.05}}}}""";
        var reply = server.ReplyOnce("application/json", Add, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var scope = alice.BrainId;
        await host.Get<IApp>(IApp.Key(scope, "calculator")).Create(Calculator(), "spec");

        var outcome = await host.Get<IRouter>(scope).Dispatch("Use the calculator to add 17+25 and tell me the sum", null, ct);

        Assert.False(outcome.Dispatched);
        Assert.Equal(("calculator", "add", 0.95), (outcome.Route.App, outcome.Route.Operation, outcome.Route.Confidence));
        Assert.Null(outcome.Result);
        Assert.Equal("The operation needs input filled from the request, so it was not carried out directly.", outcome.Error);
        await reply;
    }

    // A toolbar button posts "" (AppToolbarEntry), so its operation's input needs no filling.
    [Fact]
    public async Task AToolbarButtonOperationIsDispatchedWithTheInputItsButtonPosts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Clear = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.clear","confidence":0.95,"probabilities":{"calculator.clear":0.95,"something else":0.05}}}}""";
        var reply = server.ReplyOnce("application/json", Clear, ct);
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var app = host.Get<IApp>(IApp.Key(alice.BrainId, "calculator"));
        await app.Create(Calculator(), "spec");
        await using var requested = await host.Observe<AppRequested>(app, ct);

        var outcome = await host.Get<IRouter>(alice.BrainId).Dispatch("clear the calculator display", null, ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        var request = await requested.NextAsync(ct: ct);
        Assert.Equal(("clear", ""), (request.Operation, request.Input));
        await reply;
    }

    // Seen live: a goal asked again read to Jev as already done, because each step question showed the
    // router's memory of the earlier run, and the run ended with no step. A goal's progress is its own steps.
    [Fact]
    public async Task AGoalAskedAgainIsCarriedOutAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var replies = Enumerable.Range(0, 6).Select(_ => server.ReplyOnce("application/json", DoneOnceTheNoteIsSet, ct)).ToArray();
        var scope = "";
        var model = Answering(() => $$"""{"key":"{{scope}}/notepad","arguments":["hello from Jev"]}""");
        await using var host = await Start(server.Url, model, ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        scope = alice.BrainId;
        var router = host.Get<IRouter>(scope);

        var first = await router.Achieve("write a note for me", [typeof(IText).FullName!], ct);
        var second = await router.Achieve("write a note for me", [typeof(IText).FullName!], ct);

        Assert.Equal("Set", Assert.Single(first.Steps).Method);
        Assert.Equal("Set", Assert.Single(second.Steps).Method);
        await Task.WhenAll(replies);
    }

    // A Jev that reads its prompt: it sets the note, and once the prompt shows the note set it says the goal is done.
    private static string DoneOnceTheNoteIsSet(string body)
    {
        using var request = System.Text.Json.JsonDocument.Parse(body);
        if (request.RootElement.GetProperty("questions").TryGetProperty("method", out _))
        { return """{"model":"jev","answers":{"method":{"type":"choice","choice":"Set","confidence":0.95,"probabilities":{"Set":0.95}}}}"""; }
        var next = (request.RootElement.GetProperty("state").GetString() ?? "").Contains("IText.Set", StringComparison.Ordinal) ? "finish" : "text";
        return $$$$$"""{"model":"jev","answers":{"next":{"type":"choice","choice":"{{{{{next}}}}}","confidence":0.95,"probabilities":{"{{{{{next}}}}}":0.95}}}}""";
    }

    [Fact]
    public async Task ALaterRouteUsesTheAppMenuWithoutReadingTheAppsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Open = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.open","confidence":0.95,"probabilities":{"calculator.open":0.95,"something else":0.05}}}}""";
        const string Clear = """{"model":"jev","answers":{"app_0":{"type":"choice","choice":"calculator.clear","confidence":0.95,"probabilities":{"calculator.clear":0.95,"something else":0.05}}}}""";
        var first = server.ReplyOnce("application/json", Open, ct);
        var second = server.ReplyOnce("application/json", Clear, ct);
        var reads = new AppMenuReads();
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct, services =>
        {
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(AppMenuHost)));
            services.AddSingleton<IOutgoingGrainCallFilter>(reads);
        });
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        var catalog = host.Services.GetRequiredService<AppOperationCatalog>();
        catalog.Replace(alice.BrainId, [("calculator", [new MenuOperation("calculator", "open", "open the keypad")])]);
        var router = host.Get<IRouter>(alice.BrainId);
        reads.Armed = true;

        await router.Ask(new RouteRequest(Guid.NewGuid(), "open the calculator"), ct);
        Assert.Contains("calculator.open", (await first).Body, StringComparison.Ordinal);
        Assert.Equal(0, reads.Reads);

        catalog.Upsert(alice.BrainId, "calculator", [new MenuOperation("calculator", "clear", "empty the display")]);
        await router.Ask(new RouteRequest(Guid.NewGuid(), "clear the calculator"), ct);
        var asked = (await second).Body;
        Assert.Contains("calculator.clear", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("calculator.open", asked, StringComparison.Ordinal);
        Assert.Equal(0, reads.Reads);
    }

    private sealed class AppMenuReads : IOutgoingGrainCallFilter
    {
        public bool Armed;
        public int Reads;
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (Armed && context.InterfaceMethod?.DeclaringType == typeof(IApp) && context.InterfaceMethod.Name == nameof(IApp.Read))
            { Interlocked.Increment(ref Reads); }
            return context.Invoke();
        }
    }

    private const string NotClear = "The request was not clear enough to route.";

    // The shipped calculator's operations: "open", "clear" on a toolbar button, and "add", whose input is a sum.
    private static AppManifest Calculator()
        => new("Calculator", "keypad", [new("open", "open the keypad"), new("clear", "empty the display"), new("add", "add numbers written as a+b")], [], [],
            Toolbar: [new("clear", Icon: "backspace", Label: "Clear")]);

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
