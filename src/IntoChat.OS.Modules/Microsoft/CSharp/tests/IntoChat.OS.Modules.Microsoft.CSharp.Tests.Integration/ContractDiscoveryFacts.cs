using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class ContractDiscoveryFacts(CSharpHost host)
{
    [Fact]
    public async Task DiscoveryListsComposedModulesAndReadsTheirContractsWithoutTheRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var discovery = brain.Services.GetRequiredService<CSharpContractDiscovery>();

        var index = await AsOwner(brain, () => discovery.Read([], ct));
        Assert.Contains(index.Modules, module => module.Id == "csharp");
        Assert.DoesNotContain(index.Modules, module => module.Id == "registry");
        Assert.Empty(index.Contracts);

        var csharp = await AsOwner(brain, () => discovery.Read(["csharp"], ct));
        Assert.Contains(csharp.Contracts, line => line.Contains(typeof(ICSharpFile).FullName!, StringComparison.Ordinal));


        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => AsOwner(brain, () => discovery.Read(["nope"], ct)));
        Assert.Contains("Installed module IDs", unknown.Message, StringComparison.Ordinal);
    }

    private static async Task<T> AsOwner<T>(ITestBrain brain, Func<Task<T>> read)
    {
        using (brain.Owner.AsCaller()) { return await read(); }
    }
}
