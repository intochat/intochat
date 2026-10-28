using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Integrations;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StorageV1 = DigitalBrain.Capabilities.Storage.IStorage;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

// The kernel's side of capability resolution (05-07, R4-02): the runtime broker asks it for a brain's connections in Settings
// order, binds the ones it picks and raises a connection request when none satisfies.
public static class KernelCapabilities
{
    // The os bindings the runtime registry accepts: a storage@1 module whose neuron implements only the 0.6 ILegacyStorage
    // contract passes module validation and is bound through LegacyStorageAdapter (D-03, until Phase 7).
    public static void Configure(CapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AcceptBinding(Capability.Storage.V1, typeof(ILegacyStorage));
    }

    // Call before the silo is configured: ConfigureTopologySilo validates every composed module's Provides with this registry.
    public static IHostApplicationBuilder AddKernelCapabilities(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureCapabilities(Configure);
        builder.Services.TryAddSingleton<ICapabilityConnections, KernelCapabilityConnections>();
        // secrets@1 and identity@1 (05-12): the runtime capabilities over ports to the Authority vault and directory.
        builder.Services.AddSecretsCapability<AuthorityVaultPort>();
        builder.Services.AddIdentityCapability<AuthorityDirectoryPort>();
        return builder;
    }
}

// ICapabilityConnections over the brain's connection requests neuron: ConnectionSummary.Order is the Settings order, its Provides
// the module's ModuleManifest.Provides; only the brain's own connection keys bind.
public sealed class KernelCapabilityConnections(IServiceProvider plain, IServiceProvider services, ILogger<KernelCapabilityConnections>? logger = null)
    : ICapabilityConnections
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public async Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct)
    {
        var summaries = await Requests(brain).ListConnections().WaitAsync(ct).ConfigureAwait(false);
        return [.. summaries.OrderBy(summary => summary.Order).Select(ToConnection)];
    }

    public Task<TCapability> Bind<TCapability>(BrainId brain, string key, string callerApp, CancellationToken ct) where TCapability : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var (owner, connector, _) = ConnectionKeys.Parse(key);
        if (owner != brain.Value) { throw new ArgumentException("The connection belongs to another brain.", nameof(key)); }
        _logger.LogDebug("Binding {Capability} of connection {Connection} for {App}", typeof(TCapability).Name, key, callerApp);
        object bound = typeof(TCapability) == typeof(StorageV1)
            ? BindStorage(key, connector)
            : plain.GetNeuron<TCapability>(services, key);
        return Task.FromResult((TCapability)bound);
    }

    // Dedupes on the brain's pending requests: one pending request of a module providing the capability is enough.
    public async Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct)
    {
        var requests = Requests(brain);
        var kinds = (await requests.ListKinds().WaitAsync(ct).ConfigureAwait(false)).Select(kind => kind.Id).ToList();
        var hosted = services.GetService<ConnectionNeuronTypes>();
        var candidates = kinds.Where(kind => hosted?.ProvidesOf(kind).Any(provided => provided.Satisfies(capability)) == true).ToList();
        if (candidates.Count == 0)
        {
            _logger.LogInformation("{App} needs {Capability} in brain {Brain}; no module offered here provides it", callerApp, capability, brain.Value);
            return;
        }
        var pending = await requests.ListPending().WaitAsync(ct).ConfigureAwait(false);
        if (pending.Any(request => candidates.Contains(request.Kind, StringComparer.Ordinal))) { return; }
        var request = await requests.Request(candidates[0]).WaitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("{App} needs {Capability} in brain {Brain}: requested a {Kind} connection ({Request})", callerApp, capability, brain.Value, request.Kind, request.Id);
    }

    private IConnectionRequests Requests(BrainId brain) => SiloNeurons.Get<IConnectionRequests>(plain, IConnectionRequests.Key(brain.Value));

    // A neuron implementing storage@1 itself is reached through its own contract (ConnectionStorage keeps streams host-local);
    // one implementing only the 0.6 contract through LegacyStorageAdapter (D-03).
    private StorageV1 BindStorage(string key, string connector)
    {
        var neuron = services.GetService<ConnectionNeuronTypes>()?.NeuronOf(connector);
        if (neuron is not null && typeof(StorageV1).IsAssignableFrom(neuron))
        {
            var id = ConnectionGrains.IdFor(key);
            var uploads = typeof(IStorageUpload).IsAssignableFrom(neuron) ? (IStorageUpload)SiloNeurons.Get(plain, typeof(IStorageUpload), id) : null;
            return new ConnectionStorage((StorageV1)SiloNeurons.Get(plain, typeof(StorageV1), id), key, uploads, services.GetService<IStorageWriteAuthorizer>());
        }
        return new LegacyStorageAdapter(plain.GetNeuron<ILegacyStorage>(services, key), connector, connector);
    }

    private static BrainConnection ToConnection(ConnectionSummary summary)
        => new(summary.Neuron, summary.Account is { Length: > 0 } account ? account : summary.Id, summary.Status == "connected",
            [.. summary.Provides.Select(text => CapabilityId.TryParse(text, out var id) ? id : (CapabilityId?)null).OfType<CapabilityId>()],
            ConnectionStorage.Root(summary.Neuron));
}
