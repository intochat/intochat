using DigitalBrain.AI;
using DigitalBrain.AI.Routing;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.AI.SystemOne;
using DigitalBrain.AI.SystemOne.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Time.Timers;
using ITimer = DigitalBrain.Time.Timers.ITimer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

// The Jev router reads the deployed contract catalog, picks a capability with a System One model,
// and fills the call with a language model. Here it routes "set a timer" to ITimer.Start.
public sealed class SystemOneRouterFacts
{
    private const string Principal = "alice";
    private const string BrainName = "brain";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;

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
        var model = StubChatClient.Replying($$"""{"key":"{{Scope}}/timer","arguments":["00:05:00",null]}""");
        await using var host = await Start(server.Url, model, ct);
        Stamp();
        await host.AuthorizeCallerAsync();

        var outcome = await host.Get<IRouter>(Scope).Dispatch("set a timer for 5 minutes", null, ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        Assert.Equal(typeof(ITimer).FullName, outcome.Route.Contract);
        Assert.Equal("Start", outcome.Route.Method);
        Assert.Equal(Scope + "/timer", outcome.Key);
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
        Stamp();
        await host.AuthorizeCallerAsync();

        var route = await host.Get<IRouter>(Scope).Route("start a countdown please", null, ct);

        Assert.Equal(typeof(ITimer).FullName, route.Contract);
        Assert.Equal("Start", route.Method);
        Assert.Equal(0.91, route.Confidence);
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
        var model = StubChatClient.Replying($$"""{"key":"{{Scope}}/notepad","arguments":["hello from Jev"]}""");
        await using var host = await Start(server.Url, model, ct);
        Stamp();
        await host.AuthorizeCallerAsync();

        var run = await host.Get<IRouter>(Scope).Achieve("write a note for me", null, ct);

        Assert.Single(run.Steps);
        Assert.Equal("Set", run.Steps[0].Method);
        Assert.Equal("hello from Jev", (await host.Get<IText>(Scope + "/notepad").Read()).Markdown);
        await Task.WhenAll(replies);
    }

    [Fact]
    public async Task RouteIsPublishedAsARoutedSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string Contract = """{"model":"jev","answers":{"capability_0":{"type":"choice","choice":"timer","confidence":0.91,"probabilities":{"timer":0.91}}}}""";
        const string Method = """{"model":"jev","answers":{"method":{"type":"choice","choice":"Start","confidence":0.88,"probabilities":{"Start":0.88}}}}""";
        var replies = new[] { server.ReplyOnce("application/json", Contract, ct), server.ReplyOnce("application/json", Method, ct) };
        await using var host = await Start(server.Url, StubChatClient.Replying("{}"), ct);
        Stamp();
        await host.AuthorizeCallerAsync();
        var router = host.Get<IRouter>(Scope);
        await using var routed = await host.Observe<Routed>(router, ct);

        await router.Route("start a countdown please", null, ct);

        var signal = await routed.NextAsync(ct: ct);
        Assert.Equal(typeof(ITimer).FullName, signal.Contract);
        Assert.Equal("Start", signal.Method);
        Assert.Equal(0.91, signal.Confidence);
        Assert.Equal("start a countdown please", signal.State);
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
        Stamp();
        await host.AuthorizeCallerAsync();
        var systemOne = host.Get<ISystemOne>(Scope);
        await using var decided = await host.Observe<SystemOneDecided>(systemOne, ct);

        await systemOne.Ask(new SystemOneRequest("the integration keeps failing",
            new() { ["department"] = new SystemOneQuestion.Choice("Which team?", new() { ["technical"] = null, ["billing"] = null }) }), ct);

        var signal = await decided.NextAsync(ct: ct);
        Assert.Equal("the integration keeps failing", signal.RequestSummary);
        Assert.Equal(0.78, signal.Confidence);
        Assert.Equal("technical", Assert.IsType<SystemOneAnswer.Choice>(signal.Answers["department"]).Value);
        await reply;
    }

    private static Task<ModuleBrain> Start(string endpoint, IChatClient model, CancellationToken ct) =>
        ModuleTest.Create().WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?>
            {
                [IntegrationSchema.Key("typesafe", "ApiKey")] = "test-key",
                [IntegrationSchema.Key("typesafe", "Endpoint")] = endpoint,
            },
        })
            .WithModule<AIModule>()
            .WithModule<DigitalBrain.Compute.ComputeModule>()
            .WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithModule<DigitalBrain.Time.TimeModule>()
            .WithReminders()
            .ConfigureSilo(silo => silo.Services.AddSingleton(model))
            .StartAsync(ct);

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
}