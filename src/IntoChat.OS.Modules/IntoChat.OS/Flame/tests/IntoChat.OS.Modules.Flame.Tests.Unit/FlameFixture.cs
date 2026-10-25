using IntoChat.OS.Testing.Neurons;

namespace IntoChat.OS.Modules.Flame.Tests;

// Unit tier: the plain scene neuron on TestOS only.
public sealed class FlameFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => new(NeuronRuntimeKind.TestOS);

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(FlameNeurons)];
}
