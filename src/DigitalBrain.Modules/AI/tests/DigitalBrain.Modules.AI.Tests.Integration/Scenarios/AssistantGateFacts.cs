using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Postgres;
using DigitalBrain.Testing.Model;

namespace DigitalBrain.Modules.AI.Tests.Integration.Scenarios;

// The assistant's Jev-first gate over real grains: the fake System One answers with a scripted
// confidence; above the gate the router dispatches IApp.Request and no agent turn runs, below it
// the agent turn runs with Jev's hint in its instructions. The branch lives in the assistant's
// send synapse; these facts drive the same calls the synapse makes.
public sealed class AssistantGateFacts
{
    private const string SystemPrompt = "You are the user's assistant.";

    private static string Calculator(Person person) => IApp.Key(person.BrainId, "calculator");
    private static string Conversation(Person person) => IApp.Key(person.BrainId, "ino") + "/conversations/main";

    [Trait("host", "own")]
    [Fact]
    public async Task AboveTheGateTheAssistantAnswersWithoutAnAgentTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.SystemOneOverride = request => Choose(request, "apps.app2", "Request", 0.95);
        await using var host = await Start(model, ct);
        var alice = await host.PersonAsync("alice", ct);
        model.ChatFallback = request => Fill(request, Calculator(alice), """["open",""]""");
        using var caller = alice.AsCaller();
        var app = host.Get<IApp>(Calculator(alice));
        await app.Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        await using var requested = await host.Observe<AppRequested>(app, ct);
        var router = host.Get<IRouter>(alice.BrainId);

        var route = await router.Route("open calculator", null, ct);
        Assert.True(route.Confidence >= 0.8 && route.Contract == typeof(IApp).FullName && route.Method == nameof(IApp.Request));
        Assert.Equal(("calculator", "open"), (route.App, route.Operation));
        var outcome = await router.Dispatch("open calculator", [route.Contract], ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        Assert.Empty(AllChatRequests(model));
        Assert.Equal("open", (await requested.NextAsync(ct: ct)).Operation);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task BelowTheGateTheAgentTurnRunsWithJevsHints()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.SystemOneOverride = request => Choose(request, "apps.app2", "Request", 0.41);
        await using var host = await Start(model, ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();
        await host.Get<IApp>(Calculator(alice)).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        var router = host.Get<IRouter>(alice.BrainId);

        var route = await router.Route("what can you do?", null, ct);
        Assert.True(route.Confidence < 0.8);
        var events = new List<AgentTurnEvent>();
        await foreach (var item in host.Get<IAgent>(Conversation(alice)).Run(
            AgentTurn.Text("what can you do?", instructions: SystemPrompt + "\n\n" + route.Hint(), contracts: ShippedAssistant.Contracts()), ct))
        { events.Add(item); }

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        var turn = Assert.Single(AllChatRequests(model), request => Text(request).Contains(SystemPrompt, StringComparison.Ordinal));
        Assert.Contains("Likely capabilities (Jev):", Text(turn), StringComparison.Ordinal);
        Assert.Contains(typeof(IApp).FullName + ".Request (0.41)", Text(turn), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WhenSystemOneIsUnavailableTheAssistantStillRunsTheAgentTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var host = await Modules(TestBrain.Create().WithFakeModel(model)).StartAsync(ct);
        var alice = await host.PersonAsync("alice", ct);
        using var caller = alice.AsCaller();

        await host.Get<IApp>(Calculator(alice)).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => host.Get<IRouter>(alice.BrainId).Route("open calculator", null, ct));
        var events = new List<AgentTurnEvent>();
        await foreach (var item in host.Get<IAgent>(Conversation(alice)).Run(AgentTurn.Text("open calculator", instructions: SystemPrompt), ct)) { events.Add(item); }

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.Single(AllChatRequests(model), request => Text(request).Contains(SystemPrompt, StringComparison.Ordinal));
    }

    // Jev is the fake's System One endpoint, at its root.
    private static Task<ITestBrain> Start(FakeModelServer model, CancellationToken ct)
        => Modules(TestBrain.Create().WithFakeModel(model)
            .WithIntegration(AIModule.TypeSafeIntegration, "ApiKey", "test-key")
            .WithIntegration(AIModule.TypeSafeIntegration, "Endpoint", new Uri(model.Endpoint, "/").AbsoluteUri.TrimEnd('/')))
            .StartAsync(ct);

    // Every module whose contract the shipped assistant composes, so each contract is deployed.
    private static TestBrain Modules(TestBrain brain)
        => brain.WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=platform;Username=reader")
            .WithModule<AIModule>().WithModule<AppsModule>().WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithModule<CSharpModule>().WithModule<PostgresModule>().WithModule<DigitalBrain.Microsoft.Windows.WindowsModule>()
            .WithModule<DigitalBrain.Tools.ToolsModule>().WithModule<DigitalBrain.Mcp.McpModule>().WithModule<DigitalBrain.Microsoft.Playwright.PlaywrightModule>();

    // Scripted Jev: the app tier picks the calculator's operation, the capability question picks
    // `contractId`, the method question picks `method`. The pick stays the most probable option at any
    // confidence, as a consistent System One answers; the router reads the distribution, not the Value.
    private static string Choose(JsonElement request, string contractId, string method, double confidence)
    {
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var question in request.GetProperty("questions").EnumerateObject())
        {
            var options = question.Value.GetProperty("criteria").EnumerateObject().Select(option => option.Name).ToArray();
            var pick = question.Name.StartsWith("app_", StringComparison.Ordinal)
                ? (options.Contains("calculator.open", StringComparer.Ordinal) ? "calculator.open" : options[0])
                : options.Contains(contractId) ? contractId : options.Contains(method) ? method : options[0];
            var probabilities = options.ToDictionary(option => option, option => option == pick ? confidence
                : (options.Length > 1 ? Math.Min(confidence / 2, (1 - confidence) / (options.Length - 1)) : 0.0));
            answers[question.Name] = new { type = "choice", choice = pick, probabilities, confidence };
        }
        return JsonSerializer.Serialize(new { model = "fake-jev", answers });
    }

    // The router's fill prompt gets a valid IApp.Request call; everything else keeps the echo default.
    private static FakeReply? Fill(JsonElement request, string key, string arguments)
        => Text(request).Contains("Fill one neuron call", StringComparison.Ordinal)
            ? FakeReply.Text("{\"key\":\"" + key + "\",\"arguments\":" + arguments + "}")
            : null;

    private static IEnumerable<JsonElement> AllChatRequests(FakeModelServer model)
        => LLMModel.All.SelectMany(catalogued => model.Requests(catalogued.Id));

    private static string Text(JsonElement request)
        => string.Concat(request.GetProperty("messages").EnumerateArray().SelectMany(message =>
            message.GetProperty("content").ValueKind == JsonValueKind.String
                ? [message.GetProperty("content").GetString() ?? ""]
                : message.GetProperty("content").EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString() ?? "")));
}
