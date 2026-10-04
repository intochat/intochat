using DigitalBrain.Aspire.Server;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ModuleSelectionFacts
{
    [Fact]
    public void ARegisteredModuleRunsWithoutAnyConfiguration()
    {
        var server = new DigitalBrainServerBuilder().AddModule(typeof(SampleModule));
        var modules = server.SelectModules(new ConfigurationBuilder().Build());
        Assert.Single(modules);
        Assert.IsType<SampleModule>(modules[0]);
    }

    [Fact]
    public void ADisabledModuleStaysOut()
    {
        var server = new DigitalBrainServerBuilder().AddModule(typeof(SampleModule));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DigitalBrain:Modules:sample:Enabled"] = "false" }).Build();
        Assert.Empty(server.SelectModules(configuration));
    }

    [Fact]
    public void EnablingAnUnknownModuleIsAnError()
    {
        var server = new DigitalBrainServerBuilder().AddModule(typeof(SampleModule));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DigitalBrain:Modules:ghost:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => server.SelectModules(configuration));
    }

    [ModuleId("sample")]
    public sealed class SampleModule : IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }
}
