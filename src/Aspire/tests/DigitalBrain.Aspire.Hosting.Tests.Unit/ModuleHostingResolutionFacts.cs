using DigitalBrain.Contracts;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Kernel;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ModuleHostingResolutionFacts
{
    [Fact]
    public void AModuleWithAHostingAttributeResolvesItsAdapter()
    {
        var hosting = ModuleHostingResolution.Resolve(typeof(HostedModule));
        Assert.NotNull(hosting);
        Assert.Equal("hosted", hosting!.Id);
    }

    [ModuleId("hosted")]
    [ModuleHosting("DigitalBrain.Aspire.Hosting.Tests.Unit.ModuleHostingResolutionFacts+HostedAdapter, DigitalBrain.Aspire.Hosting.Tests.Unit")]
    private sealed class HostedModule : IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

    public sealed class HostedAdapter : IDigitalBrainModuleHosting
    {
        public string Id => "hosted";
        public void Configure(DigitalBrainBuilder brain) { }
    }

    [Fact]
    public void AModuleWithoutInfrastructureResolvesToNoAdapter()
        => Assert.Null(ModuleHostingResolution.Resolve(typeof(PlainModule)));

    [ModuleId("plain")]
    private sealed class PlainModule : IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

    [Fact]
    public void AnAbsentHostingAssemblyMeansNoAdapterRatherThanAFailure()
        => Assert.Null(ModuleHostingResolution.Resolve(typeof(AbsentHostingModule)));

    [Fact]
    public void AMissingTypeInAPresentAssemblyIsAnErrorNotASilentNull()
        => Assert.Throws<InvalidOperationException>(() => ModuleHostingResolution.Resolve(typeof(StaleHostingModule)));

    [ModuleId("stale")]
    [ModuleHosting("DigitalBrain.Aspire.Hosting.Tests.Unit.NoSuchHosting, DigitalBrain.Aspire.Hosting.Tests.Unit")]
    private sealed class StaleHostingModule : IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

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
