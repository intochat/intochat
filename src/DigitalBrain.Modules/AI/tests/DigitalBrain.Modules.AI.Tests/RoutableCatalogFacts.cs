using DigitalBrain.AI;
using DigitalBrain.Framework.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

// The router's menu is the person-facing capability set. The AI module's own contracts are the
// regression the section fixes: the observed run routed "open calculator" to IGpt54.GenerateStreaming
// because the menu had every model marker. Now a marker is never in the menu.
public sealed class RoutableCatalogFacts
{
    [Fact]
    public async Task TheRoutableCatalogIsThePersonFacingCapabilitiesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create()
            .WithModule<AIModule>()
            .WithModule<DigitalBrain.Apps.AppsModule>()
            .WithModule<DigitalBrain.Time.TimeModule>()
            .WithModule<DigitalBrain.Flutter.FlutterModule>()
            .WithReminders()
            .StartAsync(ct);
        var routable = host.SiloServices.GetRequiredService<ContractCatalog>().Routable();

        Assert.DoesNotContain(routable, type => type.Id.StartsWith("ai.llm.", StringComparison.Ordinal));
        Assert.DoesNotContain(routable, type => type.Id is "agent" or "ai.systemone" or "ai.models" or "ai.router" or "tools.sources");
        Assert.Contains(routable, type => type.Id == "timer");
        Assert.Contains(routable, type => type.Id == "text");
        Assert.Contains(routable, type => type.Id == "apps.app2");
    }
}