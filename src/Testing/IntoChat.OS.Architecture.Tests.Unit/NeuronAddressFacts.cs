using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The brain of a neuron address follows BrainKey.ScopeOf. That every manifest neuron's runtime address is its grain id
// is GrainNeuronContext's own fact, in IntoChat.OS.Framework.Tests.Integration (Runtime/NeuronAddressFacts).
public sealed class NeuronAddressFacts
{
    [Theory]
    [InlineData("brain-1", null)]
    [InlineData("brain-1/x", "brain-1")]
    [InlineData("brain-1/apps/a", "brain-1")]
    [InlineData("@platform/ledger", null)]
    [InlineData("bad:brain/x", null)]
    [InlineData("/x", null)]
    public void TheBrainOfAnAddressFollowsBrainKeyScopeOf(string key, string? brain)
    {
        Assert.Equal(brain, new NeuronAddress("t", key).Brain?.Value);
        Assert.Equal(BrainKey.ScopeOf(key), new NeuronAddress("t", key).Brain?.Value);
    }
}
