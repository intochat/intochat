using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Runtime.Orleans.Hosting;
using External = IntoChat.OS.Modules.Identity.External;
using IntoChat.OS.Host.Connections;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Integrations;
using IntoChat.OS.Modules.Identity.Secrets;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;

namespace IntoChat.OS.Modules.Identity;

// The typed resolver for the Authority's own grains. Platform grains are not neurons and most are
// brain-less (accounts, ledgers, integrations), or keyed by the brain id itself, so neither
// IIntoChatOS.Neuron<T> nor a brain-relative id names them. This is the module's single place that
// turns an id into a grain reference; key strings are exactly those callers composed before.
internal sealed class AuthorityNeurons(IGrainFactory grains, IServiceProvider services, Microsoft.Extensions.Options.IOptions<IdentityHostOptions>? identity = null)
{
    private static readonly IdentityHostOptions DefaultIdentity = new();

    public IBrain Brain(string brainId) => SiloNeurons.Get<IBrain>(services, IBrain.Key(brainId));
    public IDirectoryStore Directory() => SiloNeurons.Get<IDirectoryStore>(services, IDirectoryStore.Key);
    public IOsVault Vault(string owner) => SiloNeurons.Get<IOsVault>(services, IOsVault.Key(owner));
    public IPrincipalCredential Credential(string principalId) => Resolve<IPrincipalCredential>(principalId);
    public IntoChat.OS.Modules.Identity.External.IExternalTicketReplay ExternalTicketReplay(string issuer, string jti) => Resolve<IntoChat.OS.Modules.Identity.External.IExternalTicketReplay>(IntoChat.OS.Modules.Identity.External.IExternalTicketReplay.Key(issuer, jti));
    public IIdentityDirectory Passwords() => Resolve<IIdentityDirectory>((identity?.Value ?? DefaultIdentity).DirectoryKey);

    public IIntegration Integration(string integrationId) => Resolve<IIntegration>(IntegrationNeuron.KeyPrefix + integrationId);
    public DigitalBrain.Capabilities.Connections.Catalog.IConnectionCatalog Connections(string brainId) => SiloNeurons.Get<DigitalBrain.Capabilities.Connections.Catalog.IConnectionCatalog>(services, DigitalBrain.Capabilities.Connections.Catalog.IConnectionCatalog.Key(brainId));
    public IntoChat.OS.Modules.Identity.Contracts.Entries.IBrainEntries Entries(string brainId) => Resolve<IntoChat.OS.Modules.Identity.Contracts.Entries.IBrainEntries>(brainId);
    public DigitalBrain.Capabilities.Connections.Requests.IConnectionRequests ConnectionRequests(string brainId) => SiloNeurons.Get<DigitalBrain.Capabilities.Connections.Requests.IConnectionRequests>(services, DigitalBrain.Capabilities.Connections.Requests.IConnectionRequests.Key(brainId));
    // Connection neurons keep the Framework's connector-aware resolution; a key from a caller goes
    // through the manifest check, not straight to a grain type. 06-08 repoints this seam in the composition (R4-24).
    public IConnection Connection(string connectionKey) => services.GetConnection(connectionKey);
    public IConnectionInbound ConnectionInbound(string connectionKey) => services.GetConnectionInbound(connectionKey);
    public T CallerConnection<T>(IServiceProvider callerServices, string key) where T : class => callerServices.GetNeuron<T>(services, key);

    // The identity module acting for itself, on a call that has no caller yet: registration, sign-in and startup reach the
    // module's own grains before any person is stamped, and the silo refuses a call that carries no caller. A stamped caller
    // stays as it is: those grains read it (an app is refused), so it is never replaced.
    public IDisposable? Own()
        => services.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) is DigitalBrain.Access.EdgeAuthority { Current: null } edge
            ? edge.Enter(edge.Module(IdentityModule.Id, DigitalBrain.BrainId.Kernel))
            : null;

    private T Resolve<T>(string key) where T : IGrainWithStringKey => grains.GetGrain<T>(key);
}
