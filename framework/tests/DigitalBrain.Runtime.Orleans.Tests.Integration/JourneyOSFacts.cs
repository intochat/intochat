using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration;

public interface IEchoEdge
{
    string Reply();
}

public sealed class FakeEchoEdge(string reply) : IEchoEdge
{
    public int Calls { get; private set; }

    public string Reply()
    {
        Calls++;
        return reply;
    }
}

[DefaultNeuronType("facts.echo")]
public interface IEchoContract
{
    Task<string> Say();
    Task<string> WhoAmI();
}

[Neuron("facts.echo")]
public sealed class EchoNeuron(IEchoEdge edge) : Neuron, IEchoContract
{
    public Task<string> Say() => Task.FromResult(edge.Reply());

    public Task<string> WhoAmI() => Task.FromResult($"{Id.Brain.Value}:{Context.Caller?.Brain.Value}");
}

[Module("facts-echo")]
public sealed class EchoTestModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class JourneyOSFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AFakeAddedOnceIsResolvedOnBothRuntimes(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(EchoTestModule)],
            fakes => fakes.Add<IEchoEdge, FakeEchoEdge>(new("fake")),
            TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var reply = await ada.Get<IEchoContract>(new NeuronId(ada.Brain, "facts.echo", "main")).Say();

        Assert.Equal("fake", reply);
        Assert.Equal(1, os.Fake<FakeEchoEdge>().Calls);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TwoPeopleHaveSeparateBrains(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(EchoTestModule)],
            fakes => fakes.Add<IEchoEdge, FakeEchoEdge>(new("hi")),
            TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var ben = os.Person("ben");

        Assert.NotEqual(ada.Brain, ben.Brain);
        Assert.Equal(new BrainId("ada"), ada.Brain);
        Assert.Equal(new BrainId("ben"), ben.Brain);
        Assert.Same(ada, os.Person("ada"));

        var adaIdentity = await ada.Get<IEchoContract>(new NeuronId(ada.Brain, "facts.echo", "main")).WhoAmI();
        var benIdentity = await ben.Get<IEchoContract>(new NeuronId(ben.Brain, "facts.echo", "main")).WhoAmI();

        Assert.Equal("ada:ada", adaIdentity);
        Assert.Equal("ben:ben", benIdentity);
    }

    [Fact]
    public async Task AskingForAFakeThatWasNotAddedThrowsNamingIt()
    {
        await using var os = await JourneyOS.Start(
            NeuronRuntimeKind.TestOS,
            [typeof(EchoTestModule)],
            ct: TestContext.Current.CancellationToken);

        var error = Assert.Throws<InvalidOperationException>(() => os.Fake<FakeEchoEdge>());

        Assert.Contains(nameof(FakeEchoEdge), error.Message, StringComparison.Ordinal);
    }
}
