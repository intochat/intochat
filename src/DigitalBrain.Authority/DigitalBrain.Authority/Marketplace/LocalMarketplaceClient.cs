using DigitalBrain.Framework.Platform.Marketplace;

namespace DigitalBrain.Authority.Marketplace;

internal sealed class LocalMarketplaceClient(AuthorityNeurons neurons) : IMarketplaceClient
{
    private IMarketplaceCatalog Catalog => neurons.Marketplace();
    public Task<MarketplaceRecord[]> List() => Catalog.List();
    public Task<MarketplaceRecord?> Read(string publisher, string name) => Catalog.Read(publisher, name);
    public Task Upsert(string account, MarketplaceRecord listing) => Catalog.Upsert(account, listing);
    public Task Remove(string account, string publisher, string name) => Catalog.Remove(account, publisher, name);
}
