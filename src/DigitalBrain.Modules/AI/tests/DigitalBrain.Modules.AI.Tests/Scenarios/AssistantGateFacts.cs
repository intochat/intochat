using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Postgres;
using DigitalBrain.Testing.Model;

namespace DigitalBrain.Modules.AI.Tests.Scenarios;

// The assistant's Jev-first gate over real grains: the fake System One answers with a scripted
// confidence; above the gate the router dispatches IApp.Request and no agent turn runs, below it
// the agent turn runs with Jev's hint in its instructions. The branch lives in the assistant's
// send synapse; these facts drive the same calls the synapse makes.
public sealed class AssistantGateFacts
{
    private const string Principal = "alice";
    private const string BrainName = "brain";
    private const string SystemPrompt = "You are the user's assistant.";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;
    private static readonly string Calculator = IApp.Key(Scope, "calculator");
    private static readonly string Conversation = IApp.Key(Scope, "assistant") + "/conversations/main";

    [Fact]
    public async Task AboveTheGateTheAssistantAnswersWithoutAnAgentTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.SystemOneOverride = request => Choose(request, "apps.app2", "Request", 0.95);
        model.ChatFallback = request => Fill(request, Calculator, """["open",""]""");
        await using var host = await Start(model, ct);
        Stamp();
        await host.AuthorizeCallerAsync();
        var app = host.Get<IApp>(Calculator);
        await app.Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        await using var requested = await host.SubscribeAsync<AppRequested>(app, ct);
        await using var signals = requested.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        var router = host.Get<IRouter>(Scope);

        var route = await router.Route("open calculator", null, ct);
        Assert.True(route.Confidence >= 0.8 && route.Contract == typeof(IApp).FullName && route.Method == nameof(IApp.Request));
        Assert.Equal(("calculator", "open"), (route.App, route.Operation));
        var outcome = await router.Dispatch("open calculator", [route.Contract], ct);

        Assert.True(outcome.Dispatched, outcome.Error);
        Assert.Empty(AllChatRequests(model));
        Assert.True(await signals.MoveNextAsync());
        Assert.Equal("open", signals.Current.Operation);
    }

    [Fact]
    public async Task BelowTheGateTheAgentTurnRunsWithJevsHints()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.SystemOneOverride = request => Choose(request, "apps.app2", "Request", 0.41);
        await using var host = await Start(model, ct);
        Stamp();
        await host.AuthorizeCallerAsync();
        await host.Get<IApp>(Calculator).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        var router = host.Get<IRouter>(Scope);

        var route = await router.Route("what can you do?", null, ct);
        Assert.True(route.Confidence < 0.8);
        var events = new List<AgentTurnEvent>();
        await foreach (var item in host.Get<IAgent>(Conversation).Run(
            AgentTurn.Text("what can you do?", instructions: SystemPrompt + "\n\n" + route.Hint(), contracts: ShippedAssistant.Contracts()), ct))
        { events.Add(item); }

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        var turn = Assert.Single(AllChatRequests(model), request => Text(request).Contains(SystemPrompt, StringComparison.Ordinal));
        Assert.Contains("Likely capabilities (Jev):", Text(turn), StringComparison.Ordinal);
        Assert.Contains(typeof(IApp).FullName + ".Request (0.41)", Text(turn), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenSystemOneIsUnavailableTheAssistantStillRunsTheAgentTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var host = await ModuleTest.Create().WithFakeModel(model).WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?>
            {
                [IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader",
            },
        })
            .WithModule<AIModule>().WithModule<AppsModule>().WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithModule<CSharpModule>().WithModule<PostgresModule>().WithModule<DigitalBrain.Microsoft.Windows.WindowsModule>()
            .WithModule<DigitalBrain.Tools.ToolsModule>().WithModule<DigitalBrain.Mcp.McpModule>().WithModule<DigitalBrain.Microsoft.Playwright.PlaywrightModule>().WithReminders()
            .StartAsync(ct);
        Stamp();
        await host.AuthorizeCallerAsync();

        await host.Get<IApp>(Calculator).Create(new AppManifest("Calculator", "keypad", [new("open", "open the keypad")], [], []), "spec");
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => host.Get<IRouter>(Scope).Route("open calculator", null, ct));
        var events = new List<AgentTurnEvent>();
        await foreach (var item in host.Get<IAgent>(Conversation).Run(AgentTurn.Text("open calculator", instructions: SystemPrompt), ct)) { events.Add(item); }

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.Single(AllChatRequests(model), request => Text(request).Contains(SystemPrompt, StringComparison.Ordinal));
    }

    private static Task<ModuleBrain> Start(FakeModelServer model, CancellationToken ct)
    {
        var root = new Uri(model.Endpoint, "/").AbsoluteUri.TrimEnd('/');
        return ModuleTest.Create().WithFakeModel(model).WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?>
            {
                [IntegrationSchema.Key("typesafe", "ApiKey")] = "test-key",
                [IntegrationSchema.Key("typesafe", "Endpoint")] = root,
                [IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader",
            },
        })
            .WithModule<AIModule>().WithModule<AppsModule>().WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithModule<CSharpModule>().WithModule<PostgresModule>().WithModule<DigitalBrain.Microsoft.Windows.WindowsModule>()
            .WithModule<DigitalBrain.Tools.ToolsModule>().WithModule<DigitalBrain.Mcp.McpModule>().WithModule<DigitalBrain.Microsoft.Playwright.PlaywrightModule>().WithReminders()
            .StartAsync(ct);
    }

    // Scripted Jev: the app tier picks the calculator's operation, the capability question picks
    // `contractId`, the method question picks `method`.
    private static string Choose(JsonElement request, string contractId, string method, double confidence)
    {
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var question in request.GetProperty("questions").EnumerateObject())
        {
            var options = question.Value.GetProperty("criteria").EnumerateObject().Select(option => option.Name).ToArray();
            var pick = question.Name.StartsWith("app_", StringComparison.Ordinal)
                ? (options.Contains("calculator.open", StringComparer.Ordinal) ? "calculator.open" : options[0])
                : options.Contains(contractId) ? contractId : options.Contains(method) ? method : options[0];
            var probabilities = options.ToDictionary(option => option, option => option == pick ? confidence : (options.Length > 1 ? (1 - confidence) / (options.Length - 1) : 0.0));
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

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
}