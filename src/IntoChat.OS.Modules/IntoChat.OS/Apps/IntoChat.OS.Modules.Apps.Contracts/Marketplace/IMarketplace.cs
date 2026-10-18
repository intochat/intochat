using Orleans.Metadata;

namespace IntoChat.OS.Apps;

// The platform-wide list of published apps. Everyone signed in reads it; only the app neuron
// that owns a listing writes it. The apps themselves stay in their publishers' brains.
[Alias("apps.marketplace"), DefaultGrainType("apps.marketplace")]
[Internal]
public interface IMarketplace : INeuron
{
    const string Key = "@platform/marketplace";

    [Public, Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<MarketplaceListing[]> List();
    [Public, Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<MarketplaceListing?> Read(string publisher, string name);
    Task Upsert(MarketplaceListing listing);
    Task Remove(string publisher, string name);
}
