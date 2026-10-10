using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Surface;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Scenarios;

// The shipped assistant's composed contracts over real grains: a person's database is a postgres
// connection read through neurons_call, and data is shown by requesting the Data Visualizer and
// opening the window it names in the person's workspace.
public sealed class AssistantScenarioFacts
{
    private const string Principal = "alice";
    private const string BrainName = "brain";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;
    private static readonly string Database = ConnectionKeys.For(Scope, "postgres", "supabase");
    private static readonly string Visualizer = IApp.Key(Scope, "datavisualizer");
    private static readonly string Surface = Visualizer + "/views/demo";
    private static readonly string Conversation = IApp.Key(Scope, "assistant") + "/conversations/main";

    [Fact]
    public async Task WhichTablesAreInSupabaseIsAnsweredFromThePostgresConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new ScriptedModel(
            Call(NeuronSurfaceTools.ContextName, []),
            Call(NeuronSurfaceTools.CallName, new() { ["contract"] = typeof(IPostgresServer).FullName, ["key"] = Database, ["method"] = "ReadSchema", ["arguments"] = "[{}]" }));
        await using var host = await Start(model);
        await Connect(host);
        StampAssistant();

        var events = await Turn(host, "which tables are in Supabase?", ct);

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        var results = events.OfType<AgentTurnEvent.ToolCompleted>().ToArray();
        Assert.Equal([NeuronSurfaceTools.ContextName, NeuronSurfaceTools.CallName], results.Select(result => result.Name));
        Assert.All(results, result => Assert.DoesNotContain("isError", result.Result, StringComparison.Ordinal));
        Assert.Contains("Demo", results[1].Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowDataFromDemoRequestsTheDataVisualizerAndOpensItsWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var show = JsonSerializer.Serialize(new object[] { "show", """{"title":"Demo","kind":"table","source":"sql","sql":"select * from demo"}""" }, JsonSerializerOptions.Web);
        var open = JsonSerializer.Serialize(new[] { new OpenSurfaceWindow("open-demo", "demo", "Demo", new WindowReference("surface", Surface), 0) }, JsonSerializerOptions.Web);
        var model = new ScriptedModel(
            Call(NeuronSurfaceTools.CallName, new() { ["contract"] = typeof(IApp).FullName, ["key"] = Visualizer, ["method"] = "Request", ["arguments"] = show }),
            Call(NeuronSurfaceTools.CallName, new() { ["contract"] = typeof(IWorkspace).FullName, ["key"] = Scope, ["method"] = "OpenSurface", ["arguments"] = open }));
        await using var host = await Start(model);
        await Connect(host);
        StampUser();
        await host.AuthorizeCallerAsync();
        var app = host.Get<IApp>(Visualizer);
        await app.Create(new AppManifest("Data Visualizer", "shows data", [new("show", "show a table or chart")], [], []), "spec");
        // The app's script stands in here as long as the turn runs: the request is a signal, never an answer.
        await using var requested = await host.SubscribeAsync<AppRequested>(app, ct);
        await using var signals = requested.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        StampAssistant();

        var events = await Turn(host, "show data from Demo", ct);

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.All(events.OfType<AgentTurnEvent.ToolCompleted>(), result => Assert.DoesNotContain("isError", result.Result, StringComparison.Ordinal));
        Assert.True(await signals.MoveNextAsync());
        Assert.Equal("show", signals.Current.Operation);
        Assert.Equal("""{"title":"Demo","kind":"table","source":"sql","sql":"select * from demo"}""", signals.Current.Input);
        StampUser();
        var window = Assert.Single((await host.Get<IWorkspace>(Scope).Read()).Windows);
        Assert.Equal(Surface, window.Reference.NeuronId);
    }

    // The turn runs on the assistant app's conversation, as the app would start it.
    private static async Task<List<AgentTurnEvent>> Turn(ModuleBrain host, string message, CancellationToken ct)
    {
        var events = new List<AgentTurnEvent>();
        await foreach (var item in host.Get<IAgent>(Conversation).Run(AgentTurn.Text(message, contracts: ShippedAssistant.Contracts()), ct)) { events.Add(item); }
        return events;
    }

    private static Task<ModuleBrain> Start(IChatClient model) =>
        ModuleTest.Create().WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?>
            {
                [IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader",
            },
        })
            .WithModule<AIModule>().WithModule<AppsModule>().WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithModule<CSharpModule>().WithModule<PostgresModule>().WithModule<DigitalBrain.Microsoft.Windows.WindowsModule>().WithReminders()
            // No provider is provisioned, so the turn runs on the host's own chat client: the script.
            .ConfigureSilo(silo => silo.Services
                .AddSingleton(model)
                .AddSingleton<IConnectionPostgresProviderFactory>(new DemoDatabase()))
            .StartAsync(TestContext.Current.CancellationToken);

    private static async Task Connect(ModuleBrain host)
    {
        StampUser();
        await host.AuthorizeCallerAsync();
        await host.Get<IConnection>(Database).Connect("connection-string", "Host=supabase.example;Database=postgres;Username=reader");
    }

    private static void StampUser() => CallerContextStamper.Stamp(new()
    { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    // The caller as the assistant app's turn runs: the app, re-stamped at the app proxy.
    private static void StampAssistant() => CallerContextStamper.Stamp(new()
    { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.App, AppId = IApp.Key(Scope, "assistant"), StampedBy = TrustedEdge.AppProxy });

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
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages.ToList();
            var asked = history.FindLastIndex(message => message.Role == ChatRole.User);
            var made = history.Skip(asked + 1).SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count();
            if (made < calls.Length)
            { return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + made, calls[made].Name, calls[made].Arguments)]))); }
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


