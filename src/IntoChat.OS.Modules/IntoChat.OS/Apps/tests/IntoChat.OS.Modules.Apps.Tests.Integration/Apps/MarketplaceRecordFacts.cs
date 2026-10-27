namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// 05-10: a listing's required capabilities survive the trust catalog record both ways.
public sealed class MarketplaceRecordFacts
{
    [Fact]
    public void CapabilitiesRoundTripThroughTheRecord()
    {
        MarketplaceListing listing = new("ada", new AppSource.Brain("acct", "brain"), "files", "Files", "", "1.0.0", null,
            new AppRequirements(Connectors: ["postgres"], Capabilities: ["storage@1"]));
        var record = MarketplaceNeuron.Record(listing);
        Assert.Equal(["storage@1"], record.RequiresCapabilities!);
        Assert.Equal(listing, MarketplaceNeuron.Listing(record));
    }

    [Fact]
    public void AListingThatRequiresOnlyCapabilitiesKeepsItsRequirements()
    {
        MarketplaceListing listing = new("ada", new AppSource.Brain("acct", "brain"), "files", "Files", "", "1.0.0", null,
            new AppRequirements(Capabilities: ["storage@1"]));
        var back = MarketplaceNeuron.Listing(MarketplaceNeuron.Record(listing));
        Assert.NotNull(back.Requires);
        Assert.Equal(["storage@1"], back.Requires!.Capabilities!);
    }
}
