using System.Net;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Microsoft.CSharp.Authoring;
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
        Assert.NotNull(off.Services.GetRequiredService<IntoChat.OS.Modules.Apps.IScriptSandbox>());
        Assert.NotNull(off.Services.GetRequiredService<IntoChat.OS.Modules.Microsoft.CSharp.Edge.RuntimeEdge>());
        Assert.Contains(CSharpToolService.Tools, tool => tool.Name == "csharp_contract_detail");
        using (on.Owner.AsCaller())
        {
            var detail = await on.Services.GetRequiredService<CSharpToolService>().ForScope(on.Owner.BrainId).ContractDetail(
                "csharp", typeof(IntoChat.OS.Modules.Microsoft.CSharp.Files.ICSharpFile).FullName!, "Inspect", ct);
            Assert.Contains("CSharpFileSnapshot(", Assert.Single(detail.Contracts), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CSharpModuleIsTheOnlyCSharpModule()
        => Assert.Null(typeof(CSharpModule).Assembly.GetType("IntoChat.OS.Modules.Microsoft.CSharp.CSharpAuthoringModule"));
}
