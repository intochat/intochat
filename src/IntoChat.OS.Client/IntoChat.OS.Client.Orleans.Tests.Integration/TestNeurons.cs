namespace IntoChat.OS.Client.Orleans.Tests.Integration;

// The client facts' sources: plain neurons (06-08) hosted by EmitterNeurons. A record names the emitter it came from (From is
// the emitter's key), because a [Signal] record carries no publisher of its own.
[DigitalBrain.Signal("test.client.number")]
public sealed record Number(string From, int Value) : DigitalBrain.Signal;

[DigitalBrain.Signal("test.client.members-changed")]
public sealed record MembersChanged : DigitalBrain.Signal;

[DigitalBrain.Contracts.NeuronAlias("test.emitter"), DigitalBrain.Contracts.DefaultNeuronType(TestEmitter.Type)]
public interface ITestEmitter
{
    Task Emit(int value);
}

[DigitalBrain.Neuron(Type)]
public sealed class TestEmitter : DigitalBrain.Neuron, ITestEmitter
{
    public const string Type = "test-emitter";

    public Task Emit(int value) => Publish(new Number(Id.Key, value));
}

[DigitalBrain.Contracts.NeuronAlias("test.registry"), DigitalBrain.Contracts.DefaultNeuronType(TestRegistry.Type)]
public interface ITestRegistry
{
    Task Set(string[] members);
    Task<string[]> Members();
}

public sealed class RegistryState
{
    public string[] Members { get; set; } = [];
}

[DigitalBrain.Neuron(Type)]
public sealed class TestRegistry : DigitalBrain.Neuron<RegistryState>, ITestRegistry
{
    public const string Type = "test-registry";

    public Task Set(string[] members) => Save(new RegistryState { Members = members }, new MembersChanged());
    public Task<string[]> Members() => Task.FromResult(State.Members);
}

[DigitalBrain.Module("test.client-emitters")]
public sealed class EmitterNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
