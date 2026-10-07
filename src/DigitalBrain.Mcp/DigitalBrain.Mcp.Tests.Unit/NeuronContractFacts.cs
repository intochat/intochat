using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Mcp.Tests.Unit;

public sealed class NeuronContractFacts
{
    [Fact]
    public async Task StandaloneSearchFindsTimerContractsWithoutVectorInfrastructure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDigitalBrainMcp();
        await using var provider = services.BuildServiceProvider();
        var hits = await provider.GetRequiredService<NeuronTypeSearch>().Search("timer", ct: TestContext.Current.CancellationToken);
        Assert.Contains(hits, hit => hit.Type.Contract == "DigitalBrain.Time.Timers.ITimer");
        Assert.DoesNotContain(hits, hit => hit.Type.Contract.StartsWith("DigitalBrain.Platform.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCatalogExposesNeuronContractsAndRefusesCredentialContracts()
    {
        var catalog = new ContractCatalog([typeof(INeuron).Assembly, typeof(IIntegration).Assembly,
            typeof(DigitalBrain.Time.Timers.ITimer).Assembly]);
        Assert.Equal(typeof(DigitalBrain.Time.Timers.ITimer), catalog.Find(typeof(DigitalBrain.Time.Timers.ITimer).FullName!));
        Assert.DoesNotContain(typeof(INeuron).FullName!, catalog.Neurons);
        Assert.Throws<ArgumentException>(() => catalog.Find(typeof(IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => catalog.Find("Missing.Contract"));
    }

    [Fact]
    public void DeployedContractsIncludeTheModulesShippedBesideTheMcpServer()
    {
        var catalog = new ContractCatalog(ContractCatalog.Deployed());
        Assert.Contains(typeof(DigitalBrain.Time.Timers.ITimer).FullName!, catalog.Neurons);
        Assert.DoesNotContain(catalog.Neurons, name => name.StartsWith("DigitalBrain.Platform.", StringComparison.Ordinal));
    }
}
