using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// What a behavior may call: neuron contracts of the composed modules, never a kernel grain.
public sealed class CompiledTypesFacts
{
    [Fact]
    public async Task TheModuleAllowsTheComposedModulesContractAssembliesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<DigitalBrain.Microsoft.CSharp.CSharpModule>().StartAsync(ct);

        var contracts = brain.Services.GetRequiredService<CompiledTypes>();

        Assert.Equal(typeof(ICSharpFile), contracts.Contract(typeof(ICSharpFile).FullName!));
        // Time's contracts are deployed beside this host, but its module is not composed here.
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Time.Timers.ITimer).FullName!));
    }

    [Fact]
    public void KernelGrainsAreNotInTheScriptCatalog()
    {
        var contracts = new CompiledTypes([typeof(IPinger).Assembly, typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).Assembly, typeof(DigitalBrain.Platform.PlatformHosting).Assembly]);

        Assert.Equal(typeof(IPinger), contracts.Contract(typeof(IPinger).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Platform.Workshop.IWorkshop).FullName!));
    }

    [Fact]
    public void ScriptAccountsAreVisibleAndTheCredentialRegistryIsNot()
    {
        var contracts = new CompiledTypes([
            typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly,
            typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).Assembly,
        ]);

        Assert.Equal(
            typeof(DigitalBrain.Integrations.IConnectionRequests),
            contracts.Contract(typeof(DigitalBrain.Integrations.IConnectionRequests).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!));
    }

    [Fact]
    public void TheIntegrationRegistrationIsNotANeuronSoItIsAbsentFromTheScriptCatalog()
    {
        var contracts = new CompiledTypes([typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).Assembly]);

        Assert.False(typeof(DigitalBrain.INeuron).IsAssignableFrom(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration)));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!));
    }

    [Fact]
    public void ThePlatformContractsAssemblyIsInEveryInventory()
    {
        var inventory = new ModuleInventory([]);
        Assert.Contains(typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly, inventory.ContractAssemblies());
        Assert.NotEqual(typeof(DigitalBrain.INeuron).Assembly, typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly);
    }
}
