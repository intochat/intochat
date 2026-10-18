using System.Net;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Microsoft.CSharp.Authoring;
using IntoChat.OS.Microsoft.CSharp.Edge;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

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
        Assert.NotNull(off.Services.GetRequiredService<IntoChat.OS.Apps.IScriptSandbox>());
        Assert.NotNull(off.Services.GetRequiredService<IntoChat.OS.Microsoft.CSharp.Edge.RuntimeEdge>());
    }

    [Fact]
    public void CSharpModuleIsTheOnlyCSharpModule()
        => Assert.Null(typeof(CSharpModule).Assembly.GetType("IntoChat.OS.Microsoft.CSharp.CSharpAuthoringModule"));
}
