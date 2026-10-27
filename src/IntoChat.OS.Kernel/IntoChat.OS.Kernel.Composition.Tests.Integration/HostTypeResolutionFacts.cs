using DigitalBrain.Contracts;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel.Composition;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration;

public sealed class HostTypeResolutionFacts
{
    private const string Affine = "tests.host-resolution.affine";
    private const string Reentrant = "tests.host-resolution.reentrant";
    private const string Stateless = "tests.host-resolution.stateless";
    private const string Hidden = "tests.host-resolution.hidden";

    [Theory]
    [InlineData(Affine, "neuron.host")]
    [InlineData(Reentrant, "neuron.host.reentrant")]
    [InlineData(Stateless, "neuron.host.stateless")]
    public async Task ComposedPublicContractsResolveTheirDeclaredHostPolicy(string type, string expected)
        => Assert.Equal(expected, await Composed().HostType(type));

    [Fact]
    public async Task AnUnknownTypeHasNoHostPolicy()
        => Assert.Null(await Composed().HostType("tests.host-resolution.unknown"));

    [Theory]
    [InlineData(Affine)]
    [InlineData(Reentrant)]
    [InlineData(Stateless)]
    public async Task ALoadedTypeOutsideTheCompositionIsNotExposedEvenAfterResolution(string type)
    {
        Assert.NotNull(await Composed().HostType(type));
        var uncomposed = new CompiledTypeTableGrain(new CompiledTypes([]));

        Assert.Null(await uncomposed.HostType(type));
    }

    [Fact]
    public async Task AnInternalContractIsNotExposedAsPublicHostMetadata()
        => Assert.Null(await Composed().HostType(Hidden));

    private static CompiledTypeTableGrain Composed()
        => new(new CompiledTypes([typeof(HostTypeResolutionFacts).Assembly]));

    [NeuronAlias(Affine), DefaultNeuronType(Affine)]
    public interface IAffineProbe;

    [DigitalBrain.Neuron(HostTypeResolutionFacts.Affine)]
    public sealed class AffineProbe : DigitalBrain.Neuron, IAffineProbe;

    [NeuronAlias(Reentrant), DefaultNeuronType(Reentrant)]
    public interface IReentrantProbe;

    [DigitalBrain.Reentrant]
    public abstract class ReentrantBase : DigitalBrain.Neuron;

    [DigitalBrain.Neuron(HostTypeResolutionFacts.Reentrant)]
    public sealed class ReentrantProbe : ReentrantBase, IReentrantProbe;

    [NeuronAlias(Stateless), DefaultNeuronType(Stateless)]
    public interface IStatelessProbe;

    [DigitalBrain.Neuron(HostTypeResolutionFacts.Stateless), DigitalBrain.StatelessNeuron]
    public sealed class StatelessProbe : DigitalBrain.Neuron, IStatelessProbe;

    [NeuronAlias(Hidden), DefaultNeuronType(Hidden), DigitalBrain.Access.Internal]
    public interface IHiddenProbe;

    [DigitalBrain.Neuron(HostTypeResolutionFacts.Hidden)]
    public sealed class HiddenProbe : DigitalBrain.Neuron, IHiddenProbe;
}
