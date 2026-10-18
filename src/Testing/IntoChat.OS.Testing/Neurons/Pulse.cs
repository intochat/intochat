using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Testing.Neurons;

// A kernel-known neuron that retains its last signal: what a fact subscribes to when it needs a
// real source with retention. Start the brain with WithModule<PulseModule>(). The last pulse is
// grain state, so it survives a restart.
public sealed class PulseModule() : Framework.Modules.Module(new ModuleId("pulse"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IPulse).Assembly;
    public override void Configure(ISiloBuilder silo) { }
}

[GenerateSerializer, Alias("testing.pulsed")]
public sealed record Pulsed([property: Id(0)] int Value) : Signal;

[Alias("testing.pulse"), Orleans.Metadata.DefaultGrainType("testing.pulse")]
public interface IPulse : IRetainsSignals
{
    Task Pulse(int value);
}

[GenerateSerializer, Alias("testing.pulse-state")]
public sealed class PulseState
{
    [Id(0)] public Pulsed? Last { get; set; }
}

[GrainType("testing.pulse")]
internal sealed class PulseNeuron(
    [PersistentState("pulse", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<PulseState> state) : Neuron, IPulse, IRetainsSignals
{
    public async Task Pulse(int value)
    {
        state.State.Last = new Pulsed(value) { Publisher = this.GetPrimaryKeyString() };
        await state.WriteStateAsync();
        await PublishAsync(new Pulsed(value));
    }

    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(Pulsed) && state.State.Last is { } last ? [new RetainedSignal(1, last)] : []);
}
