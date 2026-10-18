using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Flutter.Workspace;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Framework.Surface;
using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Scenarios;

// The shipped assistant's composed contracts over real grains: a person's database is a postgres
// connection read through neurons_call, and data is shown by requesting the Data Visualizer and
// opening the window it names in the person's workspace.
public sealed class AssistantScenarioFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task WhichTablesAreInSupabaseIsAnsweredFromThePostgresConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new ScriptedModel(
            Call(NeuronSurfaceTools.ContextName, []),
            Call(NeuronSurfaceTools.CallName, new() { ["contract"] = typeof(IPostgresServer).FullName, ["key"] = "{database}", ["method"] = "ReadSchema", ["arguments"] = "[{}]" }));
        await using var host = await Start(model, ct);
        var alice = await host.PersonAsync("alice", ct);
        model.Database = Database(alice);
        await Connect(host, alice);

        List<AgentTurnEvent> events;
        using (alice.AsApp(Assistant(alice))) { events = await Turn(host, alice, "which tables are in Supabase?", ct); }

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        var results = events.OfType<AgentTurnEvent.ToolCompleted>().ToArray();
        Assert.Equal([NeuronSurfaceTools.ContextName, NeuronSurfaceTools.CallName], results.Select(result => result.Name));
        Assert.All(results, result => Assert.DoesNotContain("isError", result.Result, StringComparison.Ordinal));
        Assert.Contains("Demo", results[1].Result, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ShowDataFromDemoRequestsTheDataVisualizerAndFocusesItsSphere()
    {
        var ct = TestContext.Current.CancellationToken;
        var show = JsonSerializer.Serialize(new object[] { "show", """{"title":"Demo","kind":"table","source":"sql","sql":"select * from demo"}""" }, JsonSerializerOptions.Web);
        var model = new ScriptedModel(
            Call(NeuronSurfaceTools.CallName, new() { ["contract"] = typeof(IApp).FullName, ["key"] = "{visualizer}", ["method"] = "Request", ["arguments"] = show }),
            Call(NeuronSurfaceTools.CallName, new() { ["contract"] = typeof(IWorkspace).FullName, ["key"] = "{scope}", ["method"] = "Show", ["arguments"] = "{open}" }));
        await using var host = await Start(model, ct);
        var alice = await host.PersonAsync("alice", ct);
        var visualizer = IApp.Key(alice.BrainId, "datavisualizer");
        model.Visualizer = visualizer;
        model.Scope = alice.BrainId;
        model.Open = JsonSerializer.Serialize(new object[] { visualizer, "open-demo", 0 }, JsonSerializerOptions.Web);
        await Connect(host, alice);
        var app = host.Get<IApp>(visualizer);
        using (alice.AsCaller()) { await app.Create(new AppManifest("Data Visualizer", "shows data", [new("show", "show a table or chart")], [], []), "spec"); }
        // The app's script stands in here as long as the turn runs: the request is a signal, never an answer.
        await using var requested = await host.Observe<AppRequested>(app, ct);

        List<AgentTurnEvent> events;
        using (alice.AsApp(Assistant(alice))) { events = await Turn(host, alice, "show data from Demo", ct); }

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.All(events.OfType<AgentTurnEvent.ToolCompleted>(), result => Assert.DoesNotContain("isError", result.Result, StringComparison.Ordinal));
        var request = await requested.NextAsync(ct: ct);
        Assert.Equal("show", request.Operation);
        Assert.Equal("""{"title":"Demo","kind":"table","source":"sql","sql":"select * from demo"}""", request.Input);
        using (alice.AsCaller())
        {
            Assert.Equal(visualizer, (await host.Get<IWorkspace>(alice.BrainId).Read()).Path[^1]);
        }
    }

    private static string Database(Person person) => ConnectionKeys.For(person.BrainId, "postgres", "supabase");

    private static string Assistant(Person person) => IApp.Key(person.BrainId, "ino");

    // The turn runs on the assistant app's conversation, as the app would start it.
    private static async Task<List<AgentTurnEvent>> Turn(ITestBrain host, Person person, string message, CancellationToken ct)
    {
        var events = new List<AgentTurnEvent>();
        await foreach (var item in host.Get<IAgent>(Assistant(person) + "/conversations/main").Run(AgentTurn.Text(message, contracts: ShippedAssistant.Contracts()), ct)) { events.Add(item); }
        return events;
    }

    // Every module whose contract the shipped assistant composes, so each contract is deployed.
    private static Task<ITestBrain> Start(IChatClient model, CancellationToken ct) =>
        TestBrain.Create()
            .WithModule<AIModule>().WithModule<AppsModule>().WithModule<IntoChat.OS.Flutter.FlutterModule>()
            .WithModule<CSharpModule>().WithModule<PostgresModule>().WithModule<IntoChat.OS.Microsoft.Windows.WindowsModule>()
            .WithModule<IntoChat.OS.Tools.ToolsModule>().WithModule<IntoChat.OS.Mcp.McpModule>().WithModule<IntoChat.OS.Microsoft.Playwright.PlaywrightModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=platform;Username=reader")
            .WithChatModel(model)
            // A person's database is reached through the provider factory: no Postgres runs here, the demo schema stands in.
            .ConfigureServices(services => services.AddSingleton<IConnectionPostgresProviderFactory>(new DemoDatabase()))
            .StartAsync(ct);

    private static async Task Connect(ITestBrain host, Person person)
    {
        using (person.AsCaller())
        { await host.Get<IConnection>(Database(person)).Connect("connection-string", "Host=supabase.example;Database=postgres;Username=reader"); }
    }

    private static FunctionCallContent Call(string tool, Dictionary<string, object?> arguments) => new("", tool, arguments);

    private sealed class DemoDatabase : IConnectionPostgresProviderFactory, IPostgresProvider
    {
        public IPostgresProvider ForConnectionString(string connectionString) => this;
        public Task<PostgresSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
            => Task.FromResult(new PostgresSchema("postgres", [new PostgresTableInfo("public.Demo", "table", 3, [new PostgresColumn("id", "int4", "number")])]));
        public Task<PostgresQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken)
            => Task.FromResult(new PostgresQueryResult([new("id", "int4", "number")], [["1"], ["2"], ["3"]], 3, false, 0));
        public Task<PostgresConnection> PingAsync(CancellationToken cancellationToken) => Task.FromResult(new PostgresConnection(true, "postgres", "17", "Npgsql"));
    }

    // Plays the given calls in order, one per model step, then answers with the last result.
    private sealed class ScriptedModel(params FunctionCallContent[] calls) : IChatClient
    {
        public string Database { get; set; } = "";
        public string Visualizer { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Open { get; set; } = "";

        private Dictionary<string, object?> Bound(IDictionary<string, object?>? arguments)
            => (arguments ?? new Dictionary<string, object?>()).ToDictionary(pair => pair.Key, pair => pair.Value is string text
                ? text.Replace("{database}", Database, StringComparison.Ordinal).Replace("{visualizer}", Visualizer, StringComparison.Ordinal)
                    .Replace("{scope}", Scope, StringComparison.Ordinal).Replace("{open}", Open, StringComparison.Ordinal)
                : pair.Value);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages.ToList();
            var asked = history.FindLastIndex(message => message.Role == ChatRole.User);
            var made = history.Skip(asked + 1).SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count();
            if (made < calls.Length)
            { return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + made, calls[made].Name, Bound(calls[made].Arguments))]))); }
            var last = history.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Last();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(last.Result))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) { yield return update; }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
