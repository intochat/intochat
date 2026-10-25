namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Unit;

public sealed class OrleansFacts
{
    [Fact]
    public void TheDefaultOptionsValidateAndKeepAzuriteOffButPersistent()
    {
        var options = new OrleansOptions();

        options.Validate();

        Assert.False(options.Azurite.Enabled);
        Assert.True(options.Azurite.PersistentStorage);
        Assert.Null(options.Azurite.DataVolume);
        Assert.False(string.IsNullOrWhiteSpace(options.GrainStateContainer));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankGrainStateContainerIsRefused(string container)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new OrleansOptions { GrainStateContainer = container }.Validate());

        Assert.Contains("GrainStateContainer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullAzuriteSectionIsRefused()
        => Assert.Throws<ArgumentNullException>(() => new OrleansOptions { Azurite = null! }.Validate());

    [Fact]
    public void AStoreNameAlwaysYieldsTheSameStore()
    {
        var stores = new MemoryStores();

        Assert.Same(stores.Store("grains"), stores.Store("grains"));
    }

    [Fact]
    public void StoresAreSeparatePerNameAndNamesAreCaseSensitive()
    {
        var stores = new MemoryStores();
        stores.Store("a")["k"] = ([1], 1);

        Assert.Empty(stores.Store("b"));
        Assert.Empty(stores.Store("A"));
        Assert.Equal(1, stores.Store("a")["k"].ETag);
    }

    [Fact]
    public void StoreKeysAreCaseSensitive()
    {
        var store = new MemoryStores().Store("a");
        store["Key"] = ([1], 1);
        store["key"] = ([2], 2);

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void AMemoryStoresInstanceKeepsStateAcrossAnythingThatHoldsIt()
    {
        var stores = new MemoryStores();
        stores.Store("grains")["g1"] = ([7, 8], 5);

        var (state, etag) = stores.Store("grains")["g1"];

        Assert.Equal(new byte[] { 7, 8 }, state);
        Assert.Equal(5, etag);
    }
}
