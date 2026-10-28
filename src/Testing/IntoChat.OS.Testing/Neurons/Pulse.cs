using DigitalBrain.State;

namespace IntoChat.OS.Testing.Neurons;

// A kernel-known neuron that retains its last signal: what a fact subscribes to when it needs a real source with retention.
// Start the brain with WithModule<PulseModule>(). A plain neuron (06-08): the last pulse is its saved state and its journal's
// last Pulsed, so it survives a restart and a late watcher gets it first (the runtime's retained replay).
[DigitalBrain.Module("pulse")]
public sealed class PulseModule : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
    public static System.Reflection.Assembly? Contracts => typeof(IPulse).Assembly;
}

[DigitalBrain.Signal("testing.pulsed")]
public sealed record Pulsed(int Value) : DigitalBrain.Signal;

[DigitalBrain.Contracts.NeuronAlias("testing.pulse"), DigitalBrain.Contracts.DefaultNeuronType("testing.pulse")]
public interface IPulse
{
    Task Pulse(int value);

    // The last pulse, or null before the first.
    [DigitalBrain.ReadOnlyCall] Task<Pulsed?> Last();
}

[ExportableState]
public sealed record PulseState
{
    public Pulsed? Last { get; init; }
}

[DigitalBrain.Neuron("testing.pulse", State = "pulse")]
internal sealed class PulseNeuron : DigitalBrain.Neuron<PulseState>, IPulse
{
    public async Task Pulse(int value)
    {
        var pulsed = new Pulsed(value);
        await Save(State with { Last = pulsed }, pulsed);
    }

    public Task<Pulsed?> Last() => Task.FromResult(State.Last);
}
