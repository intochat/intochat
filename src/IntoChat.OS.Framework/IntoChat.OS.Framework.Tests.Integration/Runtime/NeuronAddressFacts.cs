using System.Text.Json;

namespace IntoChat.OS.Framework.Tests.Integration.Runtime;

// Clean-core PR 4: for every neuron class in the contract manifest, the runtime SPI address of an activation is its
// grain id, so Context.Address.StorageKey (what records and publisher ids will be keyed by) equals GrainId.ToString()
// and the key is GetPrimaryKeyString(), for brain-scoped, nested and brain-less keys alike. The manifest is the
// architecture project's ContractManifest.json, copied beside this test binary.
public sealed class NeuronAddressFacts
{
    private static readonly string[] Keys = ["brain-1", "brain-1/x", "brain-1/apps/a/b", "@platform/ledger", "singleton", "brain-1/x:y"];

    [Fact]
    public void EveryManifestNeuronAddressIsItsGrainId()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ContractManifest.json")));
        var grains = manifest.RootElement.GetProperty("Grains").EnumerateObject().Select(grain => (Type: grain.Name, GrainType: grain.Value.GetString()!)).ToArray();
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
}
