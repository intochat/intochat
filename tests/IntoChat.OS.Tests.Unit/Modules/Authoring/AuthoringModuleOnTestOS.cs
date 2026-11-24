using System.Reflection;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Authoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Authoring;

// Unit authoring left the catalog as it was: the same two neuron types under the same contracts, the same flow type, step names
// and reminder, hosted wherever the catalog (`apps`) is hosted.
public sealed class AuthoringModuleOnTestOS : ModuleOnTestOSFacts<AuthoringModule>
{
    [Fact]
    public void TheNeuronsKeepTheirTypeIdsStateNamesAndKeys()
    {
        var neurons = typeof(AuthoringModule).Assembly.GetTypes()
            .Select(type => (Type: type, Neuron: type.GetCustomAttribute<NeuronAttribute>(false)))
            .Where(pair => pair.Neuron is not null)
            .Select(pair => $"{pair.Neuron!.Type}|{pair.Neuron.State ?? "state"}|{pair.Type.FullName}")
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "flow.build|state|IntoChat.OS.Modules.Apps.Flows.BuildFlow",
                "ino.synapses|state|IntoChat.OS.Modules.Apps.SynapseStudioNeuron",
            ],
            neurons);
        Assert.Equal("owner/ino.synapses/ino/synapses", ISynapseStudio.Key("owner").ToString());
        Assert.Equal("flow.build", BuildFlow.Alias);
        Assert.Equal("flow.build", BuildInner.Flow);
        Assert.Equal("build-recheck", BuildFlow.Recheck);
        Assert.Equal(
            ["author", "draft", "verify", "deploy", "install"],
            (string[])typeof(BuildFlow).GetField("Names", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);

        // The state records and the contracts stay in the catalog's contracts assembly under their names.
        Assert.Equal("IntoChat.OS.Modules.Apps.Flows.BuildInner", typeof(BuildInner).FullName);
        Assert.Equal("IntoChat.OS.Modules.Apps.SynapseStudioState", typeof(SynapseStudioState).FullName);
        Assert.Equal("ino.synapses.changed", typeof(SynapseStudioChanged).GetCustomAttribute<DigitalBrain.SignalAttribute>()!.Alias);
        foreach (var contract in (Type[])[typeof(BuildInner), typeof(SynapseStudioState), typeof(SynapseStudioChanged), typeof(ISynapseStudio),
            typeof(IBuildChoices), typeof(IDrafter), typeof(ICreator), typeof(IUnitVerifier), typeof(IPolicyModel), typeof(AppsOptions)])
        {
            Assert.Equal("IntoChat.OS.Modules.Catalog.Contracts", contract.Assembly.GetName().Name);
        }
        Assert.Equal("IntoChat.OS.Modules.Apps.ISynapseStudio", typeof(ISynapseStudio).FullName);
    }

    [Fact]
    public void TheModuleRegistersWhatTheCatalogRegisteredForAuthoring()
    {
        var services = new ServiceCollection();
        AuthoringModule.ConfigureServices(services, new ConfigurationBuilder().Build());

        Assert.Equal(
            [
                "IntoChat.OS.Modules.Apps.Creator",
                "IntoChat.OS.Modules.Apps.ICreator",
                "IntoChat.OS.Modules.Apps.ICreatorModel",
                "IntoChat.OS.Modules.Apps.IDrafter",
                "IntoChat.OS.Modules.Apps.IPolicyModel",
                "IntoChat.OS.Modules.Apps.IUnitVerifier",
            ],
            services.Select(service => service.ServiceType.FullName!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(services, service => Assert.Equal(ServiceLifetime.Singleton, service.Lifetime));
    }

    [Fact]
    public void TheModuleIsOnWhereverTheCatalogIs()
    {
        var authoring = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "authoring");
        var catalog = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "apps");

        Assert.Equal(typeof(AuthoringModule), authoring.Type);
        Assert.True(OperatingSystemModules.DeclaresModule(authoring.Type.Assembly));
        Assert.Equal(catalog.RequiresContainer, authoring.RequiresContainer);
        Assert.Contains(OperatingSystemModules.InProcessModules, module => module.Type == typeof(AuthoringModule));
        Assert.Contains(OperatingSystemModules.InProcessModules, module => module.Type == catalog.Type);
        foreach (var topology in (string[])[IntoChatOSNames.TopologyLocal, IntoChatOSNames.TopologyLocalCloud, IntoChatOSNames.TopologyCloud])
        {
            Assert.Equal(catalog.LoadsUnder(topology), authoring.LoadsUnder(topology));
            Assert.DoesNotContain(LocalRunDefaults.EnabledKey("authoring"), LocalRunDefaults.For(topology).Keys);
            Assert.DoesNotContain(LocalRunDefaults.EnabledKey("apps"), LocalRunDefaults.For(topology).Keys);
        }
    }
}
