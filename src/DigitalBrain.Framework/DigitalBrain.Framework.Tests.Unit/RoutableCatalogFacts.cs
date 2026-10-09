using DigitalBrain;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class RoutableCatalogFacts
{
    [Fact]
    public void RoutableExposesOnlyMarkedContractsWhileReadKeepsEverything()
    {
        var catalog = new ContractCatalog(new ModuleInventory([new RoutableFixtureModule()]));

        Assert.Contains(catalog.Routable(), type => type.Id == "routable.fixture");
        Assert.DoesNotContain(catalog.Routable(), type => type.Id == "routable.plain");
        Assert.Contains(catalog.Read(), type => type.Id == "routable.plain");
    }

    [Fact]
    public void IsRoutableIsFalseForUnmarkedAndUnknownContracts()
    {
        var catalog = new ContractCatalog(new ModuleInventory([new RoutableFixtureModule()]));

        Assert.True(catalog.IsRoutable(typeof(IRoutableFixture).FullName!));
        Assert.False(catalog.IsRoutable(typeof(IPlainFixture).FullName!));
        Assert.False(catalog.IsRoutable("No.Such.Contract"));
    }
}

public sealed class RoutableFixtureModule() : Module(new ModuleId(nameof(RoutableFixtureModule)))
{
    public override System.Reflection.Assembly? Contracts => typeof(RoutableFixtureModule).Assembly;
    public override void Configure(Orleans.Hosting.ISiloBuilder silo) { }
}

[Routable, Alias("routable.fixture")]
public interface IRoutableFixture : INeuron { Task Ping(); }

[Alias("routable.plain")]
public interface IPlainFixture : INeuron { Task Ping(); }