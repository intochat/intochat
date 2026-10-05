using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace DigitalBrain.Modules.Apps.Tests.Workspace;

public sealed class InstalledAppPageFacts
{
    [Fact]
    public async Task PagingUsesOnlyTrackedInstallationsAndSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FlakyGrainStorage();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddKeyedSingleton<IGrainStorage>("Default", storage)).StartAsync(ct);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var scope = BrainScope.CurrentId();
        var id = PackageId.Create("alice", "legacy");
        var revision = await brain.Get<IPackage>(id.ToString()).Commit(new(Guid.NewGuid(), null,
            new(new("Legacy", "Legacy installation", [], [], Runtime: "prompt"), ""), "Initial"));
        var app = brain.Get<IApp>(scope + "/packages/" + id);
        await app.Install(new(Guid.NewGuid(), new(id, revision.Id), new Dictionary<string, string>()));
        CallerContextStamper.Stamp(CallerContextStamper.Require() with { Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform });
        var index = brain.Get<IApps>(scope);
        await brain.DeactivateAsync(index, ct);
        await storage.ClearStateAsync("installed-apps", index.GetGrainId(), new GrainState<InstalledAppsState>());
        Caller.As("alice");
        var page = await index.Page(0, 10);
        Assert.True(page.Ready);
        Assert.Empty(page.Packages);
        await index.Track(id);
        await brain.DeactivateAsync(index, ct);
        Assert.Equal(id, Assert.Single((await index.Page(0, 10)).Packages));
    }
}
