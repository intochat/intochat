using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Http.Surface;

namespace DigitalBrain.Runtime.Http.Tests.Unit;

public sealed class LoadedContractsFacts
{
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
}
