namespace DigitalBrain.Framework.Platform.Marketplace;

// The people's Marketplace catalog, held by the trust cluster. A write acts for `account`, which the
// machine signs for; the trust side honours it only for a brain that account owns.
public interface IMarketplaceClient
{
    Task<MarketplaceRecord[]> List();

    Task<MarketplaceRecord?> Read(string publisher, string name);

    Task Upsert(string account, MarketplaceRecord listing);

    Task Remove(string account, string publisher, string name);
}
