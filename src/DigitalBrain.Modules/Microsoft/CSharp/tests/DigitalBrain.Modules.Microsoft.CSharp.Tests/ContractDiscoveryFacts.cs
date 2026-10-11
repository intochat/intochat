using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class ContractDiscoveryFacts
{
    [Fact]
    public async Task DiscoveryListsComposedModulesAndReadsTheirContractsWithoutTheRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<CSharpModule>()
            .StartAsync(ct);
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
