using System.Net;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Edge;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class AuthoringCompositionFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AuthoringOffHidesTheRoutesButKeepsTheRuntimeEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var on = await TestBrain.Create().WithModule<CSharpModule>().StartAsync(ct);
        using var available = await on.HttpClient.GetAsync("/brains/owner/csharp/", ct);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);

        await using var off = await TestBrain.Create().WithModule<CSharpModule, CSharpOptions>(options => options.Authoring = false).StartAsync(ct);
        using var unavailable = await off.HttpClient.GetAsync("/brains/owner/csharp/", ct);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        // What the Apps publish gate and the runtime edge need stays composed.
        Assert.True(off.Services.GetRequiredService<CSharpToolService>().CanRun);
        Assert.NotNull(off.Services.GetRequiredService<DigitalBrain.Apps.IScriptSandbox>());
        Assert.NotNull(off.Services.GetRequiredService<DigitalBrain.Microsoft.CSharp.Edge.RuntimeEdge>());
    }

    [Fact]
    public void CSharpModuleIsTheOnlyCSharpModule()
        => Assert.Null(typeof(CSharpModule).Assembly.GetType("DigitalBrain.Microsoft.CSharp.CSharpAuthoringModule"));
}
