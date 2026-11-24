using System.Reflection;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.Apps.Runs;
using IntoChat.OS.Modules.Apps.Synapses;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The activity read model left the catalog as it was: the same two neuron types under the same contracts, hosted wherever the
// catalog (`apps`) is hosted.
public sealed class ActivityModuleOnTestOS : ModuleOnTestOSFacts<ActivityModule>
{
    [Fact]
    public void TheNeuronsKeepTheirTypeIdsStateNamesAndKeys()
    {
        var neurons = typeof(ActivityModule).Assembly.GetTypes()
            .Select(type => (Type: type, Neuron: type.GetCustomAttribute<NeuronAttribute>(false)))
            .Where(pair => pair.Neuron is not null)
            .Select(pair => $"{pair.Neuron!.Type}|{pair.Neuron.State ?? "state"}|{pair.Type.FullName}")
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "runs|state|IntoChat.OS.Modules.Apps.Runs.RunsNeuron",
                "synapse.definitions|state|IntoChat.OS.Modules.Apps.Synapses.SynapseDefinitionsNeuron",
            ],
            neurons);
        Assert.Equal("IntoChat.OS.Modules.Apps.Runs.RunsState", typeof(RunsState).FullName);
        Assert.Equal("owner/runs/runs/index", IRuns.Key("owner").ToString());
        Assert.Equal("owner/synapse.definitions/synapses/definitions", ISynapseDefinitions.Key("owner").ToString());
        Assert.Equal("IntoChat.OS.Modules.Catalog.Contracts", typeof(IRuns).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Catalog.Contracts", typeof(ISynapseDefinitions).Assembly.GetName().Name);
    }

    [Fact]
    public void TheModuleIsOnWhereverTheCatalogIs()
    {
        var activity = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "activity");
        var catalog = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "apps");

        Assert.Equal(typeof(ActivityModule), activity.Type);
        Assert.True(OperatingSystemModules.DeclaresModule(activity.Type.Assembly));
        Assert.Equal(catalog.RequiresContainer, activity.RequiresContainer);
        Assert.Contains(OperatingSystemModules.InProcessModules, module => module.Type == typeof(ActivityModule));
        Assert.Contains(OperatingSystemModules.InProcessModules, module => module.Type == catalog.Type);
        foreach (var topology in (string[])[IntoChatOSNames.TopologyLocal, IntoChatOSNames.TopologyLocalCloud, IntoChatOSNames.TopologyCloud])
        {
            Assert.Equal(catalog.LoadsUnder(topology), activity.LoadsUnder(topology));
            Assert.DoesNotContain(LocalRunDefaults.EnabledKey("activity"), LocalRunDefaults.For(topology).Keys);
            Assert.DoesNotContain(LocalRunDefaults.EnabledKey("apps"), LocalRunDefaults.For(topology).Keys);
        }
    }
}
