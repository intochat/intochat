using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Clean-core PR 4: for every neuron class in the manifest, the runtime SPI address of an activation is its grain id,
// so Context.Address.StorageKey (what records and publisher ids will be keyed by) equals GrainId.ToString() and the
// key is GetPrimaryKeyString(), for brain-scoped, nested and brain-less keys alike.
public sealed class NeuronAddressFacts
{
    private static readonly string[] Keys = ["brain-1", "brain-1/x", "brain-1/apps/a/b", "@platform/ledger", "singleton", "brain-1/x:y"];

    [Fact]
    public void EveryManifestNeuronAddressIsItsGrainId()
    {
        var grains = ContractManifest.Load(ContractManifest.BaselinePath).Grains;
        Assert.NotEmpty(grains);
        var offenders = new List<string>();
        foreach (var (type, grainType) in grains)
        {
            foreach (var key in Keys)
            {
                var id = GrainId.Create(grainType, key);
                var address = GrainNeuronContext.AddressOf(id);
                if (address.StorageKey != id.ToString() || address.Key != id.Key.ToString() || address.NeuronType != grainType)
                { offenders.Add($"{type} {key}: {address.StorageKey} != {id}"); }
            }
        }
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

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
