using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Http.Surface;

namespace DigitalBrain.Runtime.Http.Tests.Unit;

public sealed class LoadedContractsFacts
{
    [NeuronAlias("probe.new", Formerly = ["probe.old", "Old.Ns.IProbe"]), DefaultNeuronType("probe")]
    public interface IFormerProbe
    {
        Task Ping();
    }

    [Fact]
    public void Finds_contract_by_default_prefixes()
    {
        var found = LoadedContracts.Find(typeof(IStorage).FullName!);
        Assert.Equal(typeof(IStorage), found);
    }

    [Fact]
    public void Finds_contract_with_explicit_prefixes_and_rejects_unknown_contract()
    {
        var found = LoadedContracts.Find(typeof(IStorage).FullName!, ["CustomPrefix", "DigitalBrain"]);
        Assert.Equal(typeof(IStorage), found);

        Assert.Throws<TypeLoadException>(() => LoadedContracts.Find("NonExistent.MissingContract", ["CustomPrefix"]));
    }

    [Fact]
    public void FormerNameAndFormerAliasFindTheProbe()
    {
        Assert.Equal(typeof(IFormerProbe), LoadedContracts.Find("Old.Ns.IProbe"));
        Assert.Equal(typeof(IFormerProbe), LoadedContracts.Find("probe.old"));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName("probe.old"));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName("Old.Ns.IProbe"));
        Assert.Equal(typeof(IFormerProbe).FullName, LoadedContracts.CurrentName(typeof(IFormerProbe).FullName!));
        Assert.Equal("Unknown.Ns.IContract", LoadedContracts.CurrentName("Unknown.Ns.IContract"));
    }
}
