using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Components;

[DefaultNeuronType("facts.component.probe")]
public interface ICallerComponentProbe
{
    Task<string?> SeenComponent();
}

[Neuron("facts.component.probe")]
public sealed class CallerComponentProbeNeuron : Neuron, ICallerComponentProbe
{
    public Task<string?> SeenComponent() => Task.FromResult(Context.Caller?.Component?.Value);
}

[DefaultNeuronType("facts.component.caller")]
public interface IComponentCaller
{
    Task<string?> CallProbe(string probeKey);
    Task<string?> CallThroughRelay(string relayKey, string probeKey);
}

[Neuron("facts.component.caller")]
public sealed class ComponentCallerNeuron : Neuron, IComponentCaller
{
    public Task<string?> CallProbe(string probeKey)
        => Context.Get<ICallerComponentProbe>(new NeuronId(Id.Brain, "facts.component.probe", probeKey)).SeenComponent();

    public Task<string?> CallThroughRelay(string relayKey, string probeKey)
        => Context.Get<IUnregisteredRelay>(new NeuronId(Id.Brain, "facts.component.relay", relayKey)).ForwardToProbe(probeKey);
}

[DefaultNeuronType("facts.component.relay")]
public interface IUnregisteredRelay
{
    Task<string?> ForwardToProbe(string probeKey);
}

[Neuron("facts.component.relay")]
public sealed class UnregisteredRelayNeuron : Neuron, IUnregisteredRelay
{
    public Task<string?> ForwardToProbe(string probeKey)
        => Context.Get<ICallerComponentProbe>(new NeuronId(Id.Brain, "facts.component.probe", probeKey)).SeenComponent();
}

[Module("facts-component")]
public sealed class ComponentFactsModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class CallerComponentFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ACallMadeByANeuronCarriesThatNeuronsComponent(NeuronRuntimeKind kind)
    {
        ComponentRegistry.Register(typeof(ComponentCallerNeuron), new ComponentId("app.test-component"));

        await using var os = await JourneyOS.Start(
            kind,
            [typeof(ComponentFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var caller = ada.Get<IComponentCaller>(new NeuronId(ada.Brain, "facts.component.caller", "main"));

        var direct = await caller.CallProbe("p1");
        Assert.Equal("app.test-component", direct);

        // An unregistered neuron inherits the calling component rather than wiping it.
        var relayed = await caller.CallThroughRelay("r1", "p2");
        Assert.Equal("app.test-component", relayed);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ACallerCannotSupplyItsOwnComponent(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(ComponentFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var forged = os.Runtime.Edge
            .Person("ada", "ada", ada.Brain)
            .Hop(via: null, inside: false, component: new ComponentId("app.spoofed"));

        Assert.Equal(new ComponentId("app.spoofed"), forged.Component);

        var probeId = new NeuronId(ada.Brain, "facts.component.probe", "spoof-check");
        using (os.Runtime.Edge.Enter(forged))
        {
            var seen = await os.Runtime.Get<ICallerComponentProbe>(probeId).SeenComponent();
            Assert.Null(seen);
        }
    }
}
