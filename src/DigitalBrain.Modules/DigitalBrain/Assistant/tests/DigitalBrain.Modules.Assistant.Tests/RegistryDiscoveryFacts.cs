using System.Runtime.CompilerServices;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Apps;
using DigitalBrain.Assistant;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class RegistryDiscoveryFacts
{
    [Fact]
    public async Task AssistantDiscoveryWorksWithoutARegistryModule()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AssistantModule>().StartAsync(ct);
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "alice",
            AccountId = "alice",
            BrainId = "personal",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            var factory = new RegistryDiscoveryTools(brain.Grains);
            var tool = Assert.Single(factory.Create(() => new(BrainScope.CurrentId(), "run", "call")), item => item.Name == "discover_capabilities");
            var result = Assert.IsType<AgentToolOffer>(await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "tables" }), ct));
            Assert.Empty(result.Tools);
            Assert.Empty(Assert.IsType<RegistryDiscovery>(result.Result).Errors);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task MisspelledDatabaseRequestDiscoversToolsAlongsideAnInstalledApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = new DatabaseFixture();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithModule<AssistantModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IRegistryResourceProvider>(database)).StartAsync(ct);
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "alice",
            AccountId = "alice",
            BrainId = "personal",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            await brain.AuthorizeCallerAsync();
            var scope = BrainScope.CurrentId();
            var researcher = brain.Get<IApp>(IApp.Key(scope, "researcher"));
            await researcher.Create(new("Researcher", "Research", [new("research", "Research")], [], []), "## Scenario: Researches\nIt researches.");
            await researcher.BeginBuild(null);
            await researcher.CompleteBuild(await researcher.CurrentBuildAttempt(), new() { ["notes.md"] = "// b" }, null);

            using var services = new ServiceCollection().AddModelCatalog()
                .AddSingleton<IChatClient>(new DiscoveryModel())
                .AddSingleton<IAgentToolFactory>(new RegistryDiscoveryTools(brain.Grains))
                .AddSingleton<IAgentToolFactory>(database)
                .AddSingleton(brain.SiloServices.GetRequiredService<IAgentToolSource>()).BuildServiceProvider();
            var events = await TurnLoops.Collect(services, scope, "show me data from postges, wich tables are there?",
                ["discover_capabilities", "select_capability"], ct);

            Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
            Assert.Equal(["discover_capabilities", "select_capability", "postgres_schema"], events.OfType<AgentTurnEvent.ToolCompleted>().Select(item => item.Name));
            Assert.Contains(events.OfType<AgentTurnEvent.Text>(), item => item.Content == "research_results");
            Assert.Equal(scope, database.ReadScope);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    private sealed class DatabaseFixture : IRegistryResourceProvider, IAgentToolFactory
    {
        public string Id => "postgres";
        public string? ReadScope;
        public Task<RegistryDiscovery> Discover(CancellationToken ct)
            => Task.FromResult(new RegistryDiscovery([new("postgres", "PostgreSQL tables", ["postgres_schema"])], []));
        public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context)
            => [AIFunctionFactory.Create(() => { ReadScope = context().ScopeId; return new { tables = new[] { "research_results" } }; }, "postgres_schema")];
    }

    private sealed class DiscoveryModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            var next = results.Length switch { 0 => "discover_capabilities", 1 => "select_capability", _ => "postgres_schema" };
            if (results.Length >= 3) { return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "research_results"))); }
            Assert.Contains(options!.Tools!, tool => tool.Name == next);
            Assert.DoesNotContain(options.Tools!, tool => tool.Name.StartsWith("app_", StringComparison.Ordinal));
            if (results.Length == 1) { Assert.DoesNotContain(options.Tools!, tool => tool.Name == "postgres_schema"); }
            Dictionary<string, object?> arguments = results.Length switch { 0 => new() { ["query"] = "postges tables" }, 1 => new() { ["id"] = "postgres" }, _ => [] };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-" + results.Length, next, arguments)])));
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
