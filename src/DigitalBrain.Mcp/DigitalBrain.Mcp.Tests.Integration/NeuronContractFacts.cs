using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Mcp.Tests.Integration;

public sealed class NeuronContractFacts
{
    [Fact]
    public void TheCompiledLayerExposesNeuronContractsAndRefusesCredentialContracts()
    {
        var types = new CompiledTypes([typeof(INeuron).Assembly, typeof(IIntegration).Assembly, typeof(DigitalBrain.Time.Timers.ITimer).Assembly]);
        Assert.Equal(typeof(DigitalBrain.Time.Timers.ITimer), types.Contract(typeof(DigitalBrain.Time.Timers.ITimer).FullName!));
        Assert.DoesNotContain(types.Entries, entry => entry.Name == typeof(INeuron).FullName);
        Assert.DoesNotContain(types.Entries, entry => entry.Name == typeof(IIntegration).FullName);
        Assert.Throws<ArgumentException>(() => types.Contract(typeof(IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => types.Contract("Missing.Contract"));
    }

    [Fact]
    public void ASignalEntryCarriesItsProviderAndShape()
    {
        var types = new CompiledTypes([typeof(DigitalBrain.Time.Timers.ITimer).Assembly]);
        var tick = Assert.Single(types.Entries, entry => entry.Name == typeof(DigitalBrain.Time.Timers.Signals.TimerTick).FullName);
        Assert.Equal((TypeKind.Signal, typeof(DigitalBrain.Time.Timers.ITimer).Assembly.GetName().Name), (tick.Kind, tick.Provider));
        Assert.Contains(tick.Shape, member => member.EndsWith(" TimerId", StringComparison.Ordinal));
    }
}
