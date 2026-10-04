using DigitalBrain.Contracts;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Kernel;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ModuleHostingResolutionFacts
{
    [Fact]
    public void AModuleWithAHostingAttributeResolvesItsAdapter()
    {
        var hosting = ModuleHostingResolution.Resolve(typeof(DigitalBrain.Postgres.PostgresModule));
        Assert.NotNull(hosting);
        Assert.Equal("postgres", hosting!.Id);
    }

    [Fact]
    public void AModuleWithoutInfrastructureResolvesToNoAdapter()
        => Assert.Null(ModuleHostingResolution.Resolve(typeof(DigitalBrain.Time.TimeModule)));

    [Fact]
    public void AnAbsentHostingAssemblyMeansNoAdapterRatherThanAFailure()
        => Assert.Null(ModuleHostingResolution.Resolve(typeof(AbsentHostingModule)));

    [Fact]
    public void AHostingTypeThatIsNotAnAdapterIsAnError()
        => Assert.Throws<InvalidOperationException>(() => ModuleHostingResolution.Resolve(typeof(WrongHostingModule)));

    [ModuleId("absent")]
    [ModuleHosting("No.Such.Type, No.Such.Assembly")]
    private sealed class AbsentHostingModule : IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

    [ModuleId("wrong")]
    [ModuleHosting("System.Text.StringBuilder, System.Runtime")]
    private sealed class WrongHostingModule : IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }
}
