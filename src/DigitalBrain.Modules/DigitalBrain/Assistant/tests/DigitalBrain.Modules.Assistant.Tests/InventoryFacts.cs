using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Apps;
using DigitalBrain.Assistant;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Time;
using DigitalBrain.Time.Reminders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class InventoryFacts
{
    [Theory]
    [InlineData("list_neurons")]
    [InlineData("list_active_neurons")]
    public async Task ConversationGetsDomainAttributedInventoryAndNamedFailuresForOnlyItsBrain(string tool)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().WithModule<AppsModule>().WithModule<AssistantModule>()
            .WithModule<TimeModule>().WithReminders().ConfigureSilo(silo =>
                silo.Services.AddSingleton<IRegistryInventoryProvider>(new BrokenDomain())).StartAsync(ct);
        foreach (var name in new[] { "one", "two" })
        {
            Stamp(name);
            await host.AuthorizeCallerAsync();
            var scope = BrainScope.CurrentId();
            await host.Get<IReminder>(scope + "/standup").Start(TimeSpan.FromHours(1), TimeSpan.FromDays(1));
            await host.Get<IReminder>(scope + "/review").Start(TimeSpan.FromHours(2), TimeSpan.FromDays(1));
            var app = host.Get<IApp>(IApp.Key(scope, name));
            await app.Create(new(name, "Inventory", [], [], []), "## Scenario: Lists\nIt lists.");
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t" }, null);
        }
        foreach (var name in new[] { "one", "two" })
        {
            Stamp(name);
            var model = new InventoryModel(tool);
            using var services = new ServiceCollection().AddModelCatalog().AddSingleton<IChatClient>(model)
                .AddSingleton<IEnumerable<IAgentToolFactory>>(host.SiloServices.GetServices<IAgentToolFactory>()).BuildServiceProvider();
            var events = await TurnLoops.Collect(services, BrainScope.CurrentId(), "Which reminders and apps exist in this brain?",
                AssistantDefinition.Product.Tools, ct);
            Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
            Assert.Contains(events.OfType<AgentTurnEvent.ToolCompleted>(), item => item.Name == tool);
            Assert.NotNull(model.Inventory);
            Assert.Contains("time", model.Inventory.Domains);
            Assert.Contains("apps", model.Inventory.Domains);
            Assert.Contains("broken", model.Inventory.Domains);
            Assert.Contains(model.Inventory.Errors, error => error.Provider == "broken");
            Assert.Equal(2, model.Inventory.Records.Count(record => record.Domain == "time"));
            Assert.Equal(IApp.Key(BrainScope.CurrentId(), name),
                Assert.Single(model.Inventory.Records, record => record.Domain == "apps" && record.TypeId == "apps.app2").Key);
            Assert.All(model.Inventory.Records, record => Assert.StartsWith(BrainScope.CurrentId() + "/", record.Key, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ListSynapsesReturnsOnlySynapseTypedRecordsOfTheCallersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        // The C# module is composed so its synapse contract is in the inventory the filter reads.
        await using var host = await ModuleTest.Create().WithModule<AppsModule>().WithModule<AssistantModule>().WithModule<TimeModule>().WithModule<CSharpModule>().WithReminders()
            .ConfigureSilo(silo => silo.Services.PostConfigure<CSharpDeploymentSettings>(options => options.EdgeUrl = "http://edge.test/")).StartAsync(ct);
        Stamp("one");
        await host.AuthorizeCallerAsync();
        var scope = BrainScope.CurrentId();
        await host.Get<IReminder>(scope + "/standup").Start(TimeSpan.FromHours(1), TimeSpan.FromDays(1));
        var app = host.Get<IApp>(IApp.Key(scope, "one"));
        await app.Create(new("one", "One", [], [], []), "## Scenario: Lists\nIt lists.");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/app.cs"] = "// p" }, null);
        var model = new InventoryModel("list_synapses");
        using var services = new ServiceCollection().AddModelCatalog().AddSingleton<IChatClient>(model)
            .AddSingleton<IEnumerable<IAgentToolFactory>>(host.SiloServices.GetServices<IAgentToolFactory>()).BuildServiceProvider();
        var events = await TurnLoops.Collect(services, scope, "Which synapses exist in this brain?", AssistantDefinition.Product.Tools, ct);
        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.NotNull(model.Inventory);
        Assert.All(model.Inventory.Records, record => Assert.Equal("microsoft.csharp.file", record.TypeId));
        Assert.Contains(model.Inventory.Records, record => record.Key.EndsWith("/synapses/app.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Inventory.Records, record => record.TypeId == "reminder");
    }

    private static void Stamp(string name) => CallerContextStamper.Stamp(new()
    { PrincipalId = "owner", AccountId = "owner", BrainId = name, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    private sealed class BrokenDomain : IRegistryInventoryProvider
    {
        public string Domain => "broken";
        public Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct) => throw new IOException("unavailable");
        public Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct) => throw new IOException("unavailable");
    }

    private sealed class InventoryModel(string tool) : IChatClient
    {
        public RegistryInventory? Inventory { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            if (results.Length == 0)
            {
                Assert.Contains(options!.Tools!, offered => offered.Name == tool);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("inventory", tool, new Dictionary<string, object?>())])));
            }
            Inventory = JsonSerializer.Deserialize<RegistryInventory>(JsonSerializer.Serialize(results[^1].Result), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Inventory includes domain results and a named failure.")));
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
