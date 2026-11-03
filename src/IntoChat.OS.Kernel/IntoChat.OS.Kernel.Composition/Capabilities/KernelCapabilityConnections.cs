using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Requests;
using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StorageV1 = DigitalBrain.Capabilities.Storage.IStorage;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

public sealed record SandboxCapabilityBinding<TCapability>(
    string ConnectionKey,
    string Badge,
    bool Isolated,
    TCapability Capability) where TCapability : class;

// The kernel's side of capability resolution (05-07, R4-02): the runtime broker asks it for a brain's connections in Settings
// order, binds the ones it picks and raises a connection request when none satisfies.
public static class KernelCapabilities
{
    // storage@1 binds only a neuron that implements IStorage. The 0.6 read-only adapter is gone (07-09).
    public static void Configure(CapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
    }

    // Call before the silo is configured: ConfigureTopologySilo validates every composed module's Provides with this registry.
    public static IHostApplicationBuilder AddKernelCapabilities(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureCapabilities(Configure);
        builder.Services.TryAddSingleton<ICapabilityConnections, KernelCapabilityConnections>();
        // secrets@1 and identity@1 (05-12): the runtime capabilities over ports to the Authority vault and directory.
        builder.Services.AddSecretsCapability<VaultPort>();
        builder.Services.AddIdentityCapability<DirectoryPort>();
        return builder;
    }
}

// ICapabilityConnections over the brain's connection requests neuron: ConnectionSummary.Order is the Settings order, its Provides
// the module's ModuleManifest.Provides; only the brain's own connection keys bind.
public sealed class KernelCapabilityConnections(IServiceProvider plain, IServiceProvider services, ILogger<KernelCapabilityConnections>? logger = null)
    : ICapabilityConnections
{
    public const string SandboxCredentialsBadge = "SANDBOX / TEST CREDENTIALS";
    public const string SandboxConnectionId = "sandbox-test";

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly ConcurrentDictionary<string, LocalCloudNeuron> _localClouds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LocalCloudNeuron> _sandboxStorages = new(StringComparer.Ordinal);

    private static NeuronId LocalDiskId(BrainId brain) => new(brain, "local-disk", "main");
    private static string LocalDiskConnectionKey(BrainId brain) => $"{brain.Value}/local-disk/main";

    public static NeuronId LocalCloudId(BrainId brain) => new(brain, LocalCloudNeuron.TypeName, "main");
    public static string LocalCloudConnectionKey(BrainId brain) => $"{brain.Value}/{LocalCloudNeuron.TypeName}/main";

    public static string SandboxConnectionKey(BrainId brain, string connector = "sandbox")
        => ConnectionKeys.For(brain.Value, connector, SandboxConnectionId);

    public static bool IsSandboxCaller(string? callerApp)
    {
        if (string.IsNullOrWhiteSpace(callerApp)) { return false; }
        var normalized = callerApp.Trim();
        return string.Equals(normalized, "sandbox", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "creator-sandbox", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/sandbox", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/creator-sandbox", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("sandbox:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("test:", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct)
    {
        // The kernel reads its brain's connections as the platform: a user stamp made of the brain id is nobody's account.
        using var stampScope = RuntimeCallerContext.StampScope(CapabilityCallers.Platform(brain.Value, brain.Value, brain.Value));
        try
        {
            var summaries = await Requests(brain).ListConnections().WaitAsync(ct).ConfigureAwait(false);
            var list = summaries.OrderBy(summary => summary.Order).Select(ToConnection).ToList();
            var localKey = LocalDiskConnectionKey(brain);
            if (!list.Any(item => string.Equals(item.Key, localKey, StringComparison.Ordinal)))
            {
                try
                {
                    var localDisk = SiloNeurons.Get<StorageV1>(plain, LocalDiskId(brain));
                    var roots = await localDisk.Roots(ct).WaitAsync(ct).ConfigureAwait(false);
                    if (roots.Count > 0)
                    {
                        list.Add(new BrainConnection(
                            localKey,
                            Environment.MachineName,
                            true,
                            [Capability.Storage.V1],
                            ConnectionStorage.Root(localKey)));
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogDebug(error, "LocalDisk module is optional in isolated test hosts for {Brain}", brain.Value);
                }
            }
            return list;
        }
        finally
        {
            stampScope.Dispose();
        }
    }

    public Task<TCapability> Bind<TCapability>(BrainId brain, string key, string callerApp, CancellationToken ct) where TCapability : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var badge = IsSandboxCaller(callerApp) ? SandboxCredentialsBadge : null;

        if (string.Equals(key, LocalDiskConnectionKey(brain), StringComparison.Ordinal) && typeof(TCapability) == typeof(StorageV1))
        {
            var localStorage = new ConnectionStorage(
                SiloNeurons.Get<StorageV1>(plain, LocalDiskId(brain)),
                key,
                null,
                services.GetService<IStorageWriteAuthorizer>(),
                badge);
            return Task.FromResult((TCapability)(object)localStorage);
        }

        if (string.Equals(key, LocalCloudConnectionKey(brain), StringComparison.Ordinal) && typeof(TCapability) == typeof(StorageV1))
        {
            var localCloudStorage = new ConnectionStorage(
                ResolveLocalCloud(brain),
                key,
                null,
                services.GetService<IStorageWriteAuthorizer>(),
                badge);
            return Task.FromResult((TCapability)(object)localCloudStorage);
        }

        var (owner, connector, connectionId) = ConnectionKeys.Parse(key);
        var caller = CapabilityCallers.Of($"app:{callerApp}", owner, brain.Value, CallerKind.App, callerApp);
        var cache = services.GetService<IntoChat.OS.Authority.Identity.Grants.IEdgeGrantsCache>()
            ?? IntoChat.OS.Authority.Identity.Grants.EdgeGrantsCache.Current;
        var isBound = cache.IsConnectionBoundAsync(owner, caller, key, ct).GetAwaiter().GetResult()
            || (!string.IsNullOrWhiteSpace(caller.BrainId) && !string.Equals(caller.BrainId, owner, StringComparison.OrdinalIgnoreCase)
                && cache.IsConnectionBoundAsync(caller.BrainId, caller, key, ct).GetAwaiter().GetResult());
        if (owner != brain.Value && !isBound)
        {
            throw new ArgumentException("The connection belongs to another brain.", nameof(key));
        }

        if ((string.Equals(connectionId, SandboxConnectionId, StringComparison.Ordinal) || IsSandboxCaller(callerApp))
            && typeof(TCapability) == typeof(StorageV1)
            && services.GetService<ConnectionNeuronTypes>()?.NeuronOf(connector) is null)
        {
            var sandboxStorage = new ConnectionStorage(
                _sandboxStorages.GetOrAdd(key, _ => new LocalCloudNeuron()),
                key,
                null,
                services.GetService<IStorageWriteAuthorizer>(),
                SandboxCredentialsBadge);
            return Task.FromResult((TCapability)(object)sandboxStorage);
        }

        _logger.LogDebug("Binding {Capability} of connection {Connection} for {App}", typeof(TCapability).Name, key, callerApp);
        object bound = typeof(TCapability) == typeof(StorageV1)
            ? BindStorage(key, connector, badge)
            : plain.GetNeuron<TCapability>(services, key);
        return Task.FromResult((TCapability)bound);
    }

    public async Task<SandboxCapabilityBinding<TCapability>> BindSandboxTestCapability<TCapability>(
        BrainId brain,
        string callerApp,
        string connector = "sandbox",
        CancellationToken ct = default) where TCapability : class
    {
        var key = SandboxConnectionKey(brain, connector);
        var bound = await Bind<TCapability>(brain, key, string.IsNullOrWhiteSpace(callerApp) ? "sandbox" : callerApp, ct).ConfigureAwait(false);
        return new SandboxCapabilityBinding<TCapability>(key, SandboxCredentialsBadge, Isolated: true, bound);
    }

    // Dedupes on the brain's pending requests: one pending request of a module providing the capability is enough.
    public async Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct)
    {
        // The kernel reads its brain's connections as the platform: a user stamp made of the brain id is nobody's account.
        using var stampScope = RuntimeCallerContext.StampScope(CapabilityCallers.Platform(brain.Value, brain.Value, brain.Value));
        try
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
        finally
        {
            stampScope.Dispose();
        }
    }

    private IConnectionRequests Requests(BrainId brain) => SiloNeurons.Get<IConnectionRequests>(plain, IConnectionRequests.Key(brain.Value));

    private StorageV1 ResolveLocalCloud(BrainId brain)
    {
        try
        {
            return SiloNeurons.Get<StorageV1>(plain, LocalCloudId(brain));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Falling back to in-memory LocalCloudNeuron for {Brain}", brain.Value);
            return _localClouds.GetOrAdd(brain.Value, _ => new LocalCloudNeuron());
        }
    }

    // A neuron implementing storage@1 is reached through its own contract (ConnectionStorage keeps streams host-local).
    private StorageV1 BindStorage(string key, string connector, string? sandboxBadge = null)
    {
        var neuron = services.GetService<ConnectionNeuronTypes>()?.NeuronOf(connector);
        if (neuron is not null && typeof(StorageV1).IsAssignableFrom(neuron))
        {
            var id = ConnectionGrains.IdFor(key);
            var uploads = typeof(IStorageUpload).IsAssignableFrom(neuron) ? (IStorageUpload)SiloNeurons.Get(plain, typeof(IStorageUpload), id) : null;
            return new ConnectionStorage((StorageV1)SiloNeurons.Get(plain, typeof(StorageV1), id), key, uploads, services.GetService<IStorageWriteAuthorizer>(), sandboxBadge);
        }
        throw new InvalidOperationException($"Connection '{connector}' does not implement storage.");
    }

    private static BrainConnection ToConnection(ConnectionSummary summary)
        => new(summary.Neuron, summary.Account is { Length: > 0 } account ? account : summary.Id, summary.Status == "connected",
            [.. summary.Provides.Select(text => CapabilityId.TryParse(text, out var id) ? id : (CapabilityId?)null).OfType<CapabilityId>()],
            ConnectionStorage.Root(summary.Neuron));
}
