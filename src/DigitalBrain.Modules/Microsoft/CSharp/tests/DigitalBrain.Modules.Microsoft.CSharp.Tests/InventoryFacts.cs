using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class InventoryFacts
{
    [Fact]
    public async Task InventoryReadsOnlyTheAmbientBrainsExistingAuthoringCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().WithModule<CSharpAuthoringModule>().StartAsync(ct);
        var catalog = host.SiloServices.GetRequiredService<CSharpCatalogStore>();
        foreach (var brain in new[] { "one", "two" })
        {
            Stamp(brain);
            await host.AuthorizeCallerAsync();
            await catalog.Describe(BrainScope.CurrentId(), "file", brain + " file", "", ct);
        }
        var provider = host.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "csharp");
        foreach (var brain in new[] { "one", "two" })
        {
            Stamp(brain);
            var record = Assert.Single(await provider.Persisted(null, ct));
            Assert.Equal(brain + " file", record.Display);
            Assert.Equal(CSharpCatalogStore.FileKey(BrainScope.CurrentId(), "file"), record.Key);
            Assert.Empty(await provider.Active(null, ct));
            Assert.Empty(await provider.Persisted("other", ct));
            await catalog.Remove(BrainScope.CurrentId(), "file", ct);
            Assert.Empty(await provider.Persisted(null, ct));
        }
    }

    private static void Stamp(string brain) => CallerContextStamper.Stamp(new()
    { PrincipalId = "owner", AccountId = "owner", BrainId = brain, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
}
