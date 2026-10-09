using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// What a behavior may call: neuron contracts of the composed modules, never a platform-only one.
public sealed class ScriptContractsFacts
{
    [Fact]
    public async Task TheModuleAllowsTheComposedModulesContractAssembliesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<DigitalBrain.Microsoft.CSharp.CSharpModule>().StartAsync(ct);

        var contracts = brain.SiloServices.GetRequiredService<CompiledTypes>();

        Assert.Equal(typeof(ICSharpFile), contracts.Contract(typeof(ICSharpFile).FullName!));
        // Time's contracts are deployed beside this host, but its module is not composed here.
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Time.Timers.ITimer).FullName!));
    }

    [Fact]
    public void PlatformOnlyContractsAreNotInTheScriptCatalog()
    {
        var contracts = new CompiledTypes([typeof(IPinger).Assembly, typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).Assembly]);

        Assert.Equal(typeof(IPinger), contracts.Contract(typeof(IPinger).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IPlatformOnlyPinger).FullName!));
    }

    [Fact]
    public void TheWholePlatformAssemblyContributesNoScriptContractsEvenWithoutAttributesOnTheTypes()
    {
        var platform = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        Assert.True(PlatformAssemblyAttribute.IsPlatform(platform));
        var contracts = new CompiledTypes([platform]);

        foreach (var neuronContract in platform.GetExportedTypes().Where(type => type.IsInterface && typeof(DigitalBrain.INeuron).IsAssignableFrom(type)))
        {
            Assert.Throws<ArgumentException>(() => contracts.Contract(neuronContract.FullName!));
            Assert.True(PlatformOnlyAttribute.AppliesTo(neuronContract));
        }
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
    public void TheIntegrationRegistrationContractIsAbsentFromTheScriptCatalogBecauseReleaseReliesOnThat()
    {
        var contracts = new CompiledTypes([typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).Assembly]);

        Assert.True(PlatformOnlyAttribute.AppliesTo(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration)));
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
