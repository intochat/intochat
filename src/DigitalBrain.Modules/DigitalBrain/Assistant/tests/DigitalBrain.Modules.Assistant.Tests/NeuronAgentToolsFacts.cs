using System.Text.Json;
using DigitalBrain.AI.Tools;
using DigitalBrain.Apps;
using DigitalBrain.Assistant;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class NeuronAgentToolsFacts
{
    [Fact]
    public async Task TheToolsAreTheSharedSurface()
    {
        await using var host = await AssistantHost();
        var names = Tools(host).Select(tool => tool.Name);
        Assert.Equal([NeuronSurfaceTools.ListName, NeuronSurfaceTools.SearchName, NeuronSurfaceTools.GetName,
            NeuronSurfaceTools.CallName, NeuronSurfaceTools.ContextName], names);
    }

    [Fact]
    public async Task ACallRunsAsTheCallingAppAndAForeignAppIsRefused()
    {
        await using var host = await AssistantHost();
        StampAppCaller(brain: "alice", app: "assistant");
        var foreign = await Tool(host, NeuronSurfaceTools.CallName).InvokeAsync(new()
        {
            ["contract"] = typeof(IApp).FullName,
            ["key"] = IApp.Key(BrainScope.Create("bob", "bob").Id, "datavisualizer"),
            ["method"] = "Read",
            ["arguments"] = "[]",
        }, TestContext.Current.CancellationToken);
        Assert.True(Result(foreign).GetProperty("isError").GetBoolean());
        Assert.Contains("not available in the selected brain", Result(foreign).GetProperty("message").GetString());
    }

    [Fact]
    public async Task WrongArityIsARepairableError()
    {
        await using var host = await AssistantHost();
        StampAppCaller(brain: "alice", app: "assistant");
        var result = await Tool(host, NeuronSurfaceTools.CallName).InvokeAsync(new()
        {
            ["contract"] = typeof(IApp).FullName,
            ["key"] = "x",
            ["method"] = "Read",
            ["arguments"] = "[1,2,3]",
        }, TestContext.Current.CancellationToken);
        Assert.Contains("has no method Read taking 3 arguments", Result(result).GetProperty("message").GetString());
    }

    private static Task<ModuleBrain> AssistantHost() =>
        ModuleTest.Create().WithModule<AssistantModule>().WithModule<AppsModule>().StartAsync(TestContext.Current.CancellationToken);

    // The caller as AgentTurnLoop sees it: the assistant app, re-stamped at the app proxy.
    private static void StampAppCaller(string brain, string app) => CallerContextStamper.Stamp(new()
    {
        PrincipalId = brain,
        AccountId = brain,
        BrainId = brain,
        Kind = CallerKind.App,
        StampedBy = TrustedEdge.AppProxy,
        AppId = IApp.Key(BrainScope.Create(brain, brain).Id, app),
    });

    private static IReadOnlyList<AIFunction> Tools(ModuleBrain host) =>
        host.SiloServices.GetServices<IAgentToolFactory>().OfType<NeuronAgentTools>().Single()
            .Create(() => new("scope", "run", "call"));

    private static AIFunction Tool(ModuleBrain host, string name) => Tools(host).Single(tool => tool.Name == name);

    private static JsonElement Result(object? result) => result switch
    {
        JsonElement element => element,
        _ => JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web),
    };
}
