using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Testing.Neurons;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flame.Tests;

// Integration tier: the plain scene neuron on TestOS and on SiloHost.Local.
public sealed class FlameFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(FlameNeurons)];

    // The scene's [Signal] records live in the contracts assembly, beside the module's.
    protected override void ConfigureSilo(IServiceCollection services) => services.AddJournalPayloads(typeof(Scene.IScene).Assembly);
}
