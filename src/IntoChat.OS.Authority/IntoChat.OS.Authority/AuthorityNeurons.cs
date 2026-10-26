using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using External = IntoChat.OS.Authority.Identity.External;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Authority.Marketplace;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Connections.Platform.Secrets;

namespace IntoChat.OS.Authority;

// The typed resolver for the Authority's own grains. Platform grains are not neurons and most are
// brain-less (accounts, ledgers, integrations), or keyed by the brain id itself, so neither
// IIntoChatOS.Neuron<T> nor a brain-relative id names them. This is the module's single place that
// turns an id into a grain reference; key strings are exactly those callers composed before.
// Transitional (06-06c): grains come from the kernel composition's IAuthorityGrains, which holds the grain factory.
internal sealed class AuthorityNeurons(IAuthorityGrains grains, Microsoft.Extensions.Options.IOptions<IdentityHostOptions>? identity = null)
{
    public IBrain Brain(string brainId) => Resolve<IBrain>(brainId);
    public IBrainAuthority BrainAuthority(string brainId) => Resolve<IBrainAuthority>(brainId);
    public IAccount Account(string accountId) => Resolve<IAccount>(accountId);
    public IPrincipal Principal(string principalId) => Resolve<IPrincipal>(principalId);
    public IPrincipalCredential Credential(string principalId) => Resolve<IPrincipalCredential>(principalId);
    public External.IExternalTicketReplay ExternalTicketReplay(string issuer, string jti) => Resolve<External.IExternalTicketReplay>(External.IExternalTicketReplay.Key(issuer, jti));
    public IIdentityDirectory Directory() => Resolve<IIdentityDirectory>((identity?.Value ?? new()).DirectoryKey);

    public ILedgerGrain Ledger(string accountId) => Resolve<ILedgerGrain>(accountId);
    public IUsageGrain Usage(string brainId) => Resolve<IUsageGrain>(brainId);
    public IBrainComputeGrain Compute(string brainId) => Resolve<IBrainComputeGrain>($"{brainId}/compute");
    public ITraceJournalGrain TraceJournal(string brainId, string traceId) => Resolve<ITraceJournalGrain>($"{brainId}/compute-traces/{traceId}");
    public ITraceBudgetGrain TraceBudget(string traceId) => Resolve<ITraceBudgetGrain>(traceId);
    public ITraceBudgetGrain TraceBudget(string brainId, string traceId) => Resolve<ITraceBudgetGrain>($"{brainId}/compute-budget/{traceId}");
    public ICircleShareGrain CircleShare(string circleKey) => Resolve<ICircleShareGrain>(circleKey);

    public IIntegration Integration(string integrationId) => Resolve<IIntegration>(IntegrationNeuron.KeyPrefix + integrationId);
    public IConnectionIndex Connections(string brainId) => Resolve<IConnectionIndex>(brainId);
    public IntoChat.OS.Connections.Platform.Entries.IBrainEntries Entries(string brainId) => Resolve<IntoChat.OS.Connections.Platform.Entries.IBrainEntries>(brainId);
    public IntoChat.OS.Integrations.IConnectionRequests ConnectionRequests(string brainId) => Resolve<IntoChat.OS.Integrations.IConnectionRequests>(brainId);
    public ISecrets Secrets(string owner) => Resolve<ISecrets>(owner);
    public IntoChat.OS.Authority.Secrets.ISecretVaultValues SecretValues(string owner) => Resolve<IntoChat.OS.Authority.Secrets.ISecretVaultValues>(owner);
    public IMarketplaceCatalog Marketplace() => Resolve<IMarketplaceCatalog>(IMarketplaceCatalog.Key);

    // Connection neurons keep the Framework's connector-aware resolution; a key from a caller goes
    // through the manifest check, not straight to a grain type. 06-08 repoints this seam in the composition (R4-24).
    public IConnection Connection(string connectionKey) => grains.Connection(connectionKey);
    public IConnectionInbound ConnectionInbound(string connectionKey) => grains.ConnectionInbound(connectionKey);
    public T CallerConnection<T>(IServiceProvider services, string key) where T : IGrainWithStringKey => grains.CallerConnection<T>(services, key);

    private T Resolve<T>(string key) where T : IGrainWithStringKey => grains.Get<T>(key);
}
