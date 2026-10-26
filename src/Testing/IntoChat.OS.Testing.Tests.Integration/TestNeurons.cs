namespace IntoChat.OS.Testing.Tests.Integration;

// The harness facts' source: a plain neuron (06-08) hosted by EmitterNeurons.
[DigitalBrain.Signal("test.testing.number")]
public sealed record Number(int Value) : DigitalBrain.Signal;

[DigitalBrain.Contracts.NeuronAlias("test.emitter"), DigitalBrain.Contracts.DefaultNeuronType(TestEmitter.Type)]
public interface ITestEmitter
{
    Task Emit(int value);
}

[DigitalBrain.Neuron(Type)]
public sealed class TestEmitter : DigitalBrain.Neuron, ITestEmitter
{
    public const string Type = "test-emitter";

    public Task Emit(int value) => Publish(new Number(value));
}

[DigitalBrain.Module("test.testing-emitters")]
public sealed class EmitterNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
