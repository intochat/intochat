using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Workspace;

public sealed class InventoryFacts
{
    [Fact]
    public async Task InventoryUsesInstalledMembershipAndStorageFilesInOnlyTheCallersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PackageBrain.StartAsync(ct);
        var keys = new Dictionary<string, string>();
        foreach (var user in new[] { "alice", "bob" })
        {
            Caller.As(user);
            await host.AuthorizeCallerAsync();
            var package = PackageId.Create(user, "inventory");
            var revision = await host.Get<IPackage>(package.ToString()).Commit(host.Commit(null, PackageSamples.Researcher(user)));
            var key = BrainScope.CurrentId() + "/packages/" + package;
            keys[user] = key;
            await host.Get<IApp>(key).Install(new(Guid.NewGuid(), new(package, revision.Id), new Dictionary<string, string>()));
        }
        var provider = host.Brain.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "apps");
        foreach (var user in new[] { "alice", "bob" })
        {
            Caller.As(user);
            var records = await provider.Persisted(null, ct);
            Assert.Equal(keys[user], Assert.Single(records, r => r.TypeId == "apps.app").Key);
            Assert.Contains(records, r => r.TypeId == "microsoft.csharp.file");
            Assert.All(records, r => Assert.Contains(user + "/inventory", r.Display, StringComparison.Ordinal));
            Assert.Empty(await provider.Persisted("unknown", ct));
            await host.Brain.DeactivateAsync(host.Get<IApp>(keys[user]), ct);
            Assert.Empty(await provider.Active("apps.app", ct));
            Assert.NotEmpty(await provider.Persisted("apps.app", ct));
            await host.Get<IApp>(keys[user]).Uninstall(new(Guid.NewGuid()));
            Assert.Empty(await provider.Persisted(null, ct));
        }
    }
}
