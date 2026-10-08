using System.Net;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Edge;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class AuthoringCompositionFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AuthoringOffHidesTheRoutesButKeepsTheRuntimeEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var on = await ModuleTest.Create().WithModule<CSharpModule>().WithReminders().WithHttpEdge().StartAsync(ct);
        using var available = await on.HttpClient.GetAsync("/brains/authoring-on/csharp/", ct);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);

        await using var off = await ModuleTest.Create().WithModule<CSharpModule, CSharpOptions>(options => options.Authoring = false).WithReminders().WithHttpEdge().StartAsync(ct);
        using var unavailable = await off.HttpClient.GetAsync("/brains/authoring-off/csharp/", ct);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        // What the Apps publish gate and the runtime edge need stays composed.
        Assert.True(off.SiloServices.GetRequiredService<CSharpToolService>().CanRun);
        Assert.NotNull(off.SiloServices.GetRequiredService<DigitalBrain.Apps.IScriptSandbox>());
        Assert.NotNull(off.SiloServices.GetRequiredService<DigitalBrain.Microsoft.CSharp.Edge.RuntimeEdge>());
    }

    [Fact]
    public void CSharpModuleIsTheOnlyCSharpModule()
        => Assert.Null(typeof(CSharpModule).Assembly.GetType("DigitalBrain.Microsoft.CSharp.CSharpAuthoringModule"));
}
