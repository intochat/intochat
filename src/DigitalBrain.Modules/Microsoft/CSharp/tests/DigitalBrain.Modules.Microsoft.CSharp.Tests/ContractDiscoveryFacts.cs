using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class ContractDiscoveryFacts
{
    [Fact]
    public async Task DiscoveryListsComposedModulesAndReadsTheirContractsWithoutTheRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().WithReminders()
            .StartAsync(ct);
        var discovery = brain.SiloServices.GetRequiredService<CSharpContractDiscovery>();

        var index = await discovery.Read([], ct);
        Assert.Contains(index.Modules, module => module.Id == "csharp");
        Assert.DoesNotContain(index.Modules, module => module.Id == "registry");
        Assert.Empty(index.Contracts);

        var csharp = await discovery.Read(["csharp"], ct);
        Assert.Contains(csharp.Contracts, line => line.Contains(typeof(ICSharpFile).FullName!, StringComparison.Ordinal));


        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => discovery.Read(["nope"], ct));
        Assert.Contains("Installed module IDs", unknown.Message, StringComparison.Ordinal);
    }
}
