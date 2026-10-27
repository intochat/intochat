namespace IntoChat.OS.Framework.Tests.Integration.Brain;

// A relative id becomes "{brain}/{relative}" with its own separators kept. Resolution of plain neurons by relative and full id is
// covered by the kernel composition's facts (06-08: the grain-addressing facts left with the Framework grain neuron).
public sealed class NeuronAddressingFacts
{
    [Theory]
    [InlineData("csharp/source", "acc-brain/csharp/source")]
    [InlineData("a/b/c", "acc-brain/a/b/c")]
    public void ARelativeIdKeepsItsOwnSeparators(string relative, string full)
        => Assert.Equal(full, new NeuronId(relative).Within("acc-brain").Name);
}
