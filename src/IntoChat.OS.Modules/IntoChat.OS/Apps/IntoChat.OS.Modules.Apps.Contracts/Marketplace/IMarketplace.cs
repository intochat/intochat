using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Apps;

// The platform-wide list of published apps. Everyone signed in reads it; only the app neuron
// that owns a listing writes it. The apps themselves stay in their publishers' brains.
// A plain neuron (06-08): a cluster-scoped singleton in the system brain (R4-19, clean break).
[DigitalBrain.Contracts.NeuronAlias("apps.marketplace"), DigitalBrain.Contracts.DefaultNeuronType("apps.marketplace")]
[Internal]
public interface IMarketplace
{
    const string Key = "system/main";

    [Public, DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<MarketplaceListing[]> List();
    [Public, DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<MarketplaceListing?> Read(string publisher, string name);
    Task Upsert(MarketplaceListing listing);
    Task Remove(string publisher, string name);
}
