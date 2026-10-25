using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Mcp.Tests.Integration;

public sealed class NeuronContractFacts
{
    [Fact]
    public void TheCompiledLayerExposesNeuronContractsAndRefusesCredentialContracts()
    {
        var types = new CompiledTypes([typeof(INeuron).Assembly, typeof(IIntegration).Assembly, typeof(IntoChat.OS.Modules.Time.Timers.ITimer).Assembly]);
        Assert.Equal(typeof(IntoChat.OS.Modules.Time.Timers.ITimer), types.Contract(typeof(IntoChat.OS.Modules.Time.Timers.ITimer).FullName!));
        Assert.DoesNotContain(types.Entries, entry => entry.Name == typeof(INeuron).FullName);
        Assert.DoesNotContain(types.Entries, entry => entry.Name == typeof(IIntegration).FullName);
        Assert.Throws<ArgumentException>(() => types.Contract(typeof(IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => types.Contract("Missing.Contract"));
    }

    [Fact]
    public void ASignalEntryCarriesItsProviderAndShape()
    {
        var types = new CompiledTypes([typeof(IntoChat.OS.Modules.Time.Timers.ITimer).Assembly]);
        var tick = Assert.Single(types.Entries, entry => entry.Name == typeof(IntoChat.OS.Modules.Time.Timers.Signals.TimerTick).FullName);
        Assert.Equal((TypeKind.Signal, typeof(IntoChat.OS.Modules.Time.Timers.ITimer).Assembly.GetName().Name), (tick.Kind, tick.Provider));
        Assert.Contains(tick.Shape, member => member.EndsWith(" TimerId", StringComparison.Ordinal));
    }
}
