using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using External = IntoChat.OS.Authority.Identity.External;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Connections.Platform.Secrets;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Authority;

// The typed resolver for the Authority's own grains. Platform grains are not neurons and most are
// brain-less (accounts, ledgers, integrations), or keyed by the brain id itself, so neither
// IIntoChatOS.Neuron<T> nor a brain-relative id names them. This is the module's single place that
// turns an id into a grain reference; key strings are exactly those callers composed before.
internal sealed class AuthorityNeurons(IGrainFactory grains, IServiceProvider services, Microsoft.Extensions.Options.IOptions<IdentityHostOptions>? identity = null)
{
    public IBrain Brain(string brainId) => SiloNeurons.Get<IBrain>(services, IBrain.Key(brainId));
    public IBrainAuthority BrainAuthority(string brainId) => Resolve<IBrainAuthority>(brainId);
    public IAccount Account(string accountId) => Resolve<IAccount>(accountId);
    public IPrincipal Principal(string principalId) => Resolve<IPrincipal>(principalId);
    public IPrincipalCredential Credential(string principalId) => Resolve<IPrincipalCredential>(principalId);
    public External.IExternalTicketReplay ExternalTicketReplay(string issuer, string jti) => Resolve<External.IExternalTicketReplay>(External.IExternalTicketReplay.Key(issuer, jti));
    public IIdentityDirectory Directory() => Resolve<IIdentityDirectory>((identity?.Value ?? new()).DirectoryKey);

    public IIntegration Integration(string integrationId) => Resolve<IIntegration>(IntegrationNeuron.KeyPrefix + integrationId);
    public IConnectionIndex Connections(string brainId) => SiloNeurons.Get<IConnectionIndex>(services, IConnectionIndex.Key(brainId));
    public IntoChat.OS.Connections.Platform.Entries.IBrainEntries Entries(string brainId) => Resolve<IntoChat.OS.Connections.Platform.Entries.IBrainEntries>(brainId);
    public IntoChat.OS.Integrations.IConnectionRequests ConnectionRequests(string brainId) => SiloNeurons.Get<IntoChat.OS.Integrations.IConnectionRequests>(services, IntoChat.OS.Integrations.IConnectionRequests.Key(brainId));
    public ISecrets Secrets(string owner) => Resolve<ISecrets>(owner);
    public IntoChat.OS.Authority.Secrets.ISecretVaultValues SecretValues(string owner) => Resolve<IntoChat.OS.Authority.Secrets.ISecretVaultValues>(owner);

    // Connection neurons keep the Framework's connector-aware resolution; a key from a caller goes
    // through the manifest check, not straight to a grain type. 06-08 repoints this seam in the composition (R4-24).
    public IConnection Connection(string connectionKey) => services.GetConnection(connectionKey);
    public IConnectionInbound ConnectionInbound(string connectionKey) => services.GetConnectionInbound(connectionKey);
    public T CallerConnection<T>(IServiceProvider callerServices, string key) where T : class => callerServices.GetNeuron<T>(services, key);

    private T Resolve<T>(string key) where T : IGrainWithStringKey => grains.GetGrain<T>(key);
}
