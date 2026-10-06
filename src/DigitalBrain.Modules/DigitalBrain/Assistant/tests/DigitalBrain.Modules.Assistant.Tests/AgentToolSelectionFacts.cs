using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Assistant;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AgentToolSelectionFacts
{
    private static void StampAlice() => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = "alice",
        AccountId = "alice",
        BrainId = "personal",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    private static async Task<IApp> LiveApp(ModuleBrain brain, string scope, string name, string operation)
    {
        var app = brain.Get<IApp>(IApp.Key(scope, name));
        await app.Create(new("Tools", "Installed tools", [new(operation, "Installed")], [], []), "## Scenario: Works\nIt works.");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t" }, null);
        return app;
    }

    [Fact]
    public async Task ToolsComeFromTheLiveAppInTheCurrentBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithModule<AssistantModule>().StartAsync(ct);
        StampAlice();
        await brain.AuthorizeCallerAsync();
        var scope = BrainScope.CurrentId();
        var app = await LiveApp(brain, scope, "tools", "installed-tool");
        var registry = brain.Get<ICapabilityDiscovery>(scope);
        async Task<string[]> Discover() => (await registry.Select("apps:tools", ct))?.Tools ?? [];

        Assert.Equal([AppToolName.For("tools", "installed-tool")], await Discover());
        var selected = await Discover();
        var source = Assert.Single(brain.SiloServices.GetServices<IAgentToolSource>());
        await using (var session = await source.OpenAsync(selected, () => new(scope, "run", ""), ct))
        {
            Assert.Equal(selected, session.Tools.Select(tool => tool.Name));
        }
        // Resolve the selected tools in the real runner before it reaches the model.
        using var services = new ServiceCollection().AddSingleton(source)
            .AddSingleton<IChatClient>(new ScriptedAssistantModel()).BuildServiceProvider();
        var events = new List<AgentTurnEvent>();
        await foreach (var item in new AgentTurnRunner(services).RunAsync(new("assistant", "run", scope, [], "hello", null, ToolNames: selected), ct))
        { events.Add(item); }
        Assert.DoesNotContain(events, item => item is AgentTurnEvent.Failed);
        Assert.Contains(events, item => item is AgentTurnEvent.Completed);
        // Discovery has no model-controlled scope argument; the Registry tests cover caller isolation.
        await app.Delete();
        Assert.Empty(await Discover());
    }

    [Fact]
    public async Task AppsAreDiscoverableAcrossDeactivationWithoutAnyActivationHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithModule<AssistantModule>().StartAsync(ct);
        StampAlice();
        await brain.AuthorizeCallerAsync();
        var scope = BrainScope.CurrentId();
        var app = await LiveApp(brain, scope, "private", "ask");
        Assert.Equal([AppToolName.For("private", "ask")], (await brain.Get<ICapabilityDiscovery>(scope).Select("apps:private", ct))!.Tools);
        await brain.DeactivateAsync(app, ct);
        await brain.DeactivateAsync(brain.Get<IAppIndex>(scope), ct);
        Assert.Equal("private", Assert.Single(await brain.Get<IAppIndex>(scope).List()));
    }
}
