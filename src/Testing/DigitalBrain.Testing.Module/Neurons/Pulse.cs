using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Testing.Module.Neurons;

// A kernel-known neuron that retains its last signal: what a fact subscribes to when it needs a
// real source with retention. Start the brain with WithModule<PulseModule>().
public sealed class PulseModule() : Framework.Modules.Module(new ModuleId("pulse"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IPulse).Assembly;
    public override void Configure(ISiloBuilder silo) { }
}

[GenerateSerializer, Alias("testing.pulsed")]
public sealed record Pulsed([property: Id(0)] int Value) : Signal;

[Alias("testing.pulse"), Orleans.Metadata.DefaultGrainType("testing.pulse")]
public interface IPulse : INeuron
{
    Task Pulse(int value);
}

[GrainType("testing.pulse")]
internal sealed class PulseNeuron : Neuron, IPulse, IRetainsSignals
{
    private Pulsed? _last;

    public async Task Pulse(int value)
    {
        _last = new Pulsed(value) { Publisher = this.GetPrimaryKeyString() };
        await PublishAsync(new Pulsed(value));
    }

    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(Pulsed) && _last is not null ? [new RetainedSignal(1, _last)] : []);
}
