using System.Net;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class AuthoringCompositionFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AuthoringOffHidesTheToolsAndTheRoutesButKeepsTheScriptEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var on = await ModuleTest.Create().WithModule<CSharpModule>().WithReminders().WithHttpEdge().StartAsync(ct);
        using var available = await on.HttpClient.GetAsync("/brains/authoring-on/csharp/", ct);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        Assert.Single(on.SiloServices.GetServices<IAgentToolFactory>().OfType<CSharpAgentTools>());
        Assert.Single(on.SiloServices.GetServices<IRegistryResourceProvider>().OfType<CSharpRegistryResources>());

        await using var off = await ModuleTest.Create().WithModule<CSharpModule, CSharpOptions>(options => options.Authoring = false).WithReminders().WithHttpEdge().StartAsync(ct);
        using var unavailable = await off.HttpClient.GetAsync("/brains/authoring-off/csharp/", ct);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.Empty(off.SiloServices.GetServices<IAgentToolFactory>().OfType<CSharpAgentTools>());
        Assert.Empty(off.SiloServices.GetServices<IRegistryResourceProvider>().OfType<CSharpRegistryResources>());
        // What the Apps publish gate and the script edge need stays composed.
        Assert.True(off.SiloServices.GetRequiredService<CSharpToolService>().CanRun);
        Assert.NotNull(off.SiloServices.GetRequiredService<DigitalBrain.Apps.IScriptSandbox>());
        Assert.NotNull(off.SiloServices.GetRequiredService<ScriptEdge>());
    }

    [Fact]
    public void CSharpModuleIsTheOnlyCSharpModule()
        => Assert.Null(typeof(CSharpModule).Assembly.GetType("DigitalBrain.Microsoft.CSharp.CSharpAuthoringModule"));
}
