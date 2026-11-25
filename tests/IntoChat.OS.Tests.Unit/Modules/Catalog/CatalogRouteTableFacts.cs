using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using IntoChat.OS.Tests.Unit;
using Microsoft.AspNetCore.Hosting;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// Every HTTP route the apps module maps, pinned. A move to another module or a split of a mapping method changes only
// the lines it means to change: a line that leaves this table must appear, unchanged, in the table of its new module.
public sealed class CatalogRouteTableFacts
{
    private static readonly string[] Expected =
    [
        "DELETE /brains/{brainId}/apps/{name}",
        "DELETE /brains/{brainId}/policies/{id}",
        "GET /artcraft/catalog.json anonymous order=-100",
        "GET /artcraft/{appId} anonymous order=-100",
        "GET /artcraft/{appId}/index.html anonymous order=-100",
        "GET /artcraft/{appId}/{**assetPath} anonymous order=-100",
        "GET /brains/{brainId}/apps/",
        "GET /brains/{brainId}/apps/packages",
        "GET /brains/{brainId}/apps/skills",
        "GET /brains/{brainId}/apps/units/{alias}/export-zip",
        "GET /brains/{brainId}/apps/{name}",
        "GET /brains/{brainId}/apps/{name}/export",
        "GET /brains/{brainId}/apps/{name}/icon",
        "GET /brains/{brainId}/apps/{name}/view",
        "GET /brains/{brainId}/marketplace/",
        "GET /brains/{brainId}/marketplace/{publisher}/{name}",
        "GET /brains/{brainId}/marketplace/{publisher}/{name}/icon",
        "GET /brains/{brainId}/policies/",
        "GET /brains/{brainId}/policies/actions",
        "POST /artcraft/{appId}/install anonymous order=-100",
        "POST /brains/{brainId}/apps/",
        "POST /brains/{brainId}/apps/import",
        "POST /brains/{brainId}/apps/install",
        "POST /brains/{brainId}/apps/packages/compile",
        "POST /brains/{brainId}/apps/units/import-zip",
        "POST /brains/{brainId}/apps/{name}/author",
        "POST /brains/{brainId}/apps/{name}/command/{command}",
        "POST /brains/{brainId}/apps/{name}/implement",
        "POST /brains/{brainId}/apps/{name}/publish",
        "POST /brains/{brainId}/apps/{name}/request",
        "POST /brains/{brainId}/apps/{name}/rollback",
        "POST /brains/{brainId}/apps/{name}/signal",
        "POST /brains/{brainId}/apps/{name}/update",
        "POST /brains/{brainId}/marketplace/{publisher}/{name}/install",
        "POST /brains/{brainId}/policies/",
        "PUT /brains/{brainId}/apps/{name}/settings",
    ];

    [Fact]
    public void TheAppsModuleMapsExactlyTheseRoutes()
    {
        var routes = RouteTable.Of(new AppsModule().Configure, services => services
            .Known<INeuronProxies>()
            .Known<IBrainObserver>()
            .Known<IWebHostEnvironment>());

        Assert.True(Expected.SequenceEqual(routes), "The apps module maps:" + Environment.NewLine + string.Join(Environment.NewLine, routes));
    }
}
