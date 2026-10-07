using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Time;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class ContractCatalogFacts
{
    [Fact]
    public async Task TypesAreDiscoveredAndFoundByWordsWithoutAnEmbeddingModel()
    {
        await using var brain = await ModuleTest.Create().WithModule<TimeModule>().WithReminders()
            .StartAsync(TestContext.Current.CancellationToken);
        var types = brain.SiloServices.GetRequiredService<ContractCatalog>().Read();

        Assert.Contains(types, type => type.Id == "timer" && type.Methods.Any(method => method.Contains("Start", StringComparison.Ordinal)));
        var timer = types.Single(type => type.Id == "timer");
        Assert.Equal(typeof(DigitalBrain.Time.Timers.ITimer).FullName, timer.Contract);
        Assert.Equal("time", timer.ModuleId);
        Assert.Contains(timer.Signals!, signal => signal.Contains("TimerTick", StringComparison.Ordinal) && signal.Contains("TimerId", StringComparison.Ordinal));
        Assert.Contains(types, type => type.Id == "reminder");
        Assert.DoesNotContain(types, type => type.Id == "test.registry-clock");
        Assert.Contains(await brain.SiloServices.GetRequiredService<NeuronTypeSearch>().Search("schedule a timer", ct: TestContext.Current.CancellationToken), hit => hit.Type.Id == "timer");
    }

    [Fact]
    public async Task TheIntegrationRegistrationContractIsExcludedFromTheNeuronTypeCatalog()
    {
        await using var brain = await ModuleTest.Create()
            .StartAsync(TestContext.Current.CancellationToken);
        var types = brain.SiloServices.GetRequiredService<ContractCatalog>().Read();

        Assert.DoesNotContain(types, type => type.Id == "integration.registration" || type.Contract == typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName);
    }

    [Fact]
    public async Task TheWholePlatformAssemblyContributesNoNeuronTypesToTheCatalog()
    {
        await using var brain = await ModuleTest.Create()
            .StartAsync(TestContext.Current.CancellationToken);
        var types = brain.SiloServices.GetRequiredService<ContractCatalog>().Read();

        var platformAssembly = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        var platformContracts = platformAssembly.GetExportedTypes().Where(type => type.IsInterface).Select(type => type.FullName);
        Assert.DoesNotContain(types, type => platformContracts.Contains(type.Contract));
    }

}

public sealed class RegistryFixtureModule() : Module(new ModuleId(nameof(RegistryFixtureModule)))
{
    public override void Configure(ISiloBuilder silo) { }
}

[Alias("test.registry-clock"), Description("Schedule alarms and wake up at a requested time.")]
public interface IRegistryClock : INeuron { Task Ping(); }

[GrainType("test-registry-clock")]
public sealed class RegistryClock : Neuron, IRegistryClock
{
    public Task Ping() => Task.CompletedTask;
}
