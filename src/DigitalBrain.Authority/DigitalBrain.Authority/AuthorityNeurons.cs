using DigitalBrain.Authority.Compute;
using DigitalBrain.Authority.Identity;
using DigitalBrain.Authority.Identity.Authority;
using DigitalBrain.Authority.Integrations;
using DigitalBrain.Authority.Marketplace;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Secrets;

namespace DigitalBrain.Authority;

// The typed resolver for the Authority's own grains. Platform grains are not neurons and most are
// brain-less (accounts, ledgers, integrations), or keyed by the brain id itself, so neither
// IDigitalBrain.Neuron<T> nor a brain-relative id names them. This is the module's single place that
// turns an id into a grain reference; key strings are exactly those callers composed before.
// Transitional: the grain factory moves behind the runtime's resolver when the provider owns it.
internal sealed class AuthorityNeurons(IGrainFactory grains)
{
    public IBrain Brain(string brainId) => Resolve<IBrain>(brainId);
    public IBrainAuthority BrainAuthority(string brainId) => Resolve<IBrainAuthority>(brainId);
    public IAccount Account(string accountId) => Resolve<IAccount>(accountId);
    public IPrincipal Principal(string principalId) => Resolve<IPrincipal>(principalId);
    public IPrincipalCredential Credential(string principalId) => Resolve<IPrincipalCredential>(principalId);
    public IIdentityDirectory Directory() => Resolve<IIdentityDirectory>(IdentityGrains.Directory);

    public ILedgerGrain Ledger(string accountId) => Resolve<ILedgerGrain>(accountId);
    public IUsageGrain Usage(string brainId) => Resolve<IUsageGrain>(brainId);
    public ITraceBudgetGrain TraceBudget(string traceId) => Resolve<ITraceBudgetGrain>(traceId);
    public ICircleShareGrain CircleShare(string circleKey) => Resolve<ICircleShareGrain>(circleKey);

    public IIntegration Integration(string integrationId) => Resolve<IIntegration>(IntegrationNeuron.KeyPrefix + integrationId);
    public IConnectionIndex Connections(string brainId) => Resolve<IConnectionIndex>(brainId);
    public DigitalBrain.Integrations.IConnectionRequests ConnectionRequests(string brainId) => Resolve<DigitalBrain.Integrations.IConnectionRequests>(brainId);
    public ISecrets Secrets(string owner) => Resolve<ISecrets>(owner);
    public IMarketplaceCatalog Marketplace() => Resolve<IMarketplaceCatalog>(IMarketplaceCatalog.Key);

    // Connection neurons keep the Framework's connector-aware resolution; a key from a caller goes
    // through the manifest check, not straight to a grain type.
    public IConnection Connection(string connectionKey) => grains.GetConnection(connectionKey);
    public IConnectionInbound ConnectionInbound(string connectionKey) => grains.GetConnectionInbound(connectionKey);
    public T CallerConnection<T>(IServiceProvider services, string key) where T : IGrainWithStringKey => grains.GetNeuronGrain<T>(services, key);

    private T Resolve<T>(string key) where T : IGrainWithStringKey => grains.GetGrain<T>(key);
}
