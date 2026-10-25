using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// What a behavior may call: neuron contracts of the composed modules, never a kernel grain.
public sealed class CompiledTypesFacts(CSharpHost host)
{
    [Fact]
    public async Task TheModuleAllowsTheComposedModulesContractAssembliesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;

        var contracts = brain.Services.GetRequiredService<CompiledTypes>();

        Assert.Equal(typeof(ICSharpFile), contracts.Contract(typeof(ICSharpFile).FullName!));
        // Time's contracts are deployed beside this host, but its module is not composed here.
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IntoChat.OS.Modules.Time.Timers.ITimer).FullName!));
    }

    [Fact]
    public void KernelGrainsAreNotInTheScriptCatalog()
    {
        var contracts = new CompiledTypes([typeof(IPinger).Assembly, typeof(IntoChat.OS.Connections.Platform.Secrets.ISecrets).Assembly, typeof(IntoChat.OS.Authority.PlatformHosting).Assembly]);

        Assert.Equal(typeof(IPinger), contracts.Contract(typeof(IPinger).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IntoChat.OS.Connections.Platform.Secrets.ISecrets).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IntoChat.OS.Connections.Platform.Integrations.IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IntoChat.OS.Authority.Workshop.IWorkshop).FullName!));
    }

    [Fact]
    public void ScriptAccountsAreVisibleAndTheCredentialRegistryIsNot()
    {
        var contracts = new CompiledTypes([
            typeof(IntoChat.OS.Integrations.IConnectionRequests).Assembly,
            typeof(IntoChat.OS.Connections.Platform.Connections.IConnectionIndex).Assembly,
        ]);

        Assert.Equal(
            typeof(IntoChat.OS.Integrations.IConnectionRequests),
            contracts.Contract(typeof(IntoChat.OS.Integrations.IConnectionRequests).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IntoChat.OS.Connections.Platform.Connections.IConnectionIndex).FullName!));
    }

    [Fact]
    public void TheIntegrationRegistrationIsNotANeuronSoItIsAbsentFromTheScriptCatalog()
    {
        var contracts = new CompiledTypes([typeof(IntoChat.OS.Connections.Platform.Integrations.IIntegration).Assembly]);

        Assert.False(typeof(IntoChat.OS.INeuron).IsAssignableFrom(typeof(IntoChat.OS.Connections.Platform.Integrations.IIntegration)));
        Assert.Throws<ArgumentException>(() => contracts.Contract(typeof(IntoChat.OS.Connections.Platform.Integrations.IIntegration).FullName!));
    }

    [Fact]
    public void ThePlatformContractsAssemblyIsInEveryInventory()
    {
        var inventory = new ModuleInventory([]);
        Assert.Contains(typeof(IntoChat.OS.Integrations.IConnectionRequests).Assembly, inventory.ContractAssemblies());
        Assert.NotEqual(typeof(IntoChat.OS.INeuron).Assembly, typeof(IntoChat.OS.Integrations.IConnectionRequests).Assembly);
    }
}
