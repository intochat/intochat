using DigitalBrain;
using DigitalBrain.Capabilities;
using Capability = DigitalBrain.Capabilities.Capability;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Kernel.Composition.Capabilities;
using IntoChat.OS.Modules.Google.Drive;
using System.Reflection;
using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using StorageV1 = DigitalBrain.Capabilities.Storage.IStorage;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

// 05-07: the kernel lists a brain's connections for the runtime broker in Settings order, binds only its own keys and raises a
// connection request through the brain's connection requests when nothing satisfies a capability.
public sealed class KernelCapabilityConnectionsFacts
{
    private const string Brain = "b1";
    private static readonly string Drive = ConnectionKeys.For(Brain, "google-drive", "home");
    private static readonly string Pc = ConnectionKeys.For(Brain, "windows", "pc");
    private static readonly string Legacy = ConnectionKeys.For(Brain, LegacyOnlyNeurons.Connector, "old");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectionsAreListedInSettingsOrderWithTheirProvides()
    {
        var requests = new FakeRequests
        {
            Connections =
            [
                new("windows", "pc", Pc, "connected", null, null) { Provides = ["storage@1"], Order = 1 },
                new("google-drive", "home", Drive, "pending", "a@b.c", null) { Provides = ["storage@1", "not a capability"], Order = 0 },
            ],
        };
        var connections = await Kernel(requests).ListInOrder(new BrainId(Brain), Ct);

        Assert.Equal([Drive, Pc], connections.Select(connection => connection.Key));
        Assert.Equal([false, true], connections.Select(connection => connection.Connected));
        Assert.Equal("a@b.c", connections[0].Label);
        Assert.All(connections, connection => Assert.Equal([Capability.Storage.V1], connection.Provides));
    }

    [Fact]
    public async Task TheBrokerReturnsConnectedStorageInSettingsOrder()
    {
        var requests = new FakeRequests
        {
            Connections =
            [
                new("windows", "pc", Pc, "connected", null, null) { Provides = ["storage@1"], Order = 1 },
                new("google-drive", "home", Drive, "connected", null, null) { Provides = ["storage@1"], Order = 0 },
            ],
        };
        var services = Services(requests);
        var broker = new CapabilityBroker(Registry(), services.GetRequiredService<ICapabilityConnections>(), services, []);

        var storages = await broker.Storages(new BrainId(Brain), Ct);

        Assert.Equal([Drive, Pc], storages.Select(storage => storage.ConnectionKey));
        // A neuron implementing storage@1 is bound natively (05-08 Windows, 05-09 Drive), a legacy-only one through the adapter.
        var hosted = services.GetRequiredService<ConnectionNeuronTypes>();
        Assert.All(storages, storage => Assert.IsType(
            typeof(StorageV1).IsAssignableFrom(hosted.NeuronOf(ConnectionKeys.Parse(storage.ConnectionKey).ConnectorId)) ? typeof(ConnectionStorage) : typeof(LegacyStorageAdapter),
            storage.Capability));
        Assert.IsType<ConnectionStorage>(storages[1].Capability);
    }

    [Fact]
    public async Task AMissingCapabilityRaisesOnePendingRequestOfAModuleProvidingIt()
    {
        var requests = new FakeRequests { Kinds = [new("github", "GitHub"), new("google-drive", "Google Drive")] };
        var kernel = Kernel(requests);

        await kernel.RequestConnection(new BrainId(Brain), Capability.Storage.V1, "files", Ct);
        Assert.Equal(["google-drive"], requests.Requested);

        requests.Pending = [new("google-drive", "x")];
        await kernel.RequestConnection(new BrainId(Brain), Capability.Storage.V1, "files", Ct);
        Assert.Equal(["google-drive"], requests.Requested);

        await kernel.RequestConnection(new BrainId(Brain), Capability.Secrets.V1, "files", Ct);
        Assert.Equal(["google-drive"], requests.Requested);
    }

    [Fact]
    public async Task OnlyTheBrainsOwnConnectionsBindAndLegacyStorageIsAdapted()
    {
        var kernel = Kernel(new FakeRequests());

        await Assert.ThrowsAsync<ArgumentException>(() => kernel.Bind<StorageV1>(new BrainId("b2"), Drive, "files", Ct));
        Assert.IsType<LegacyStorageAdapter>(await kernel.Bind<StorageV1>(new BrainId(Brain), Legacy, "files", Ct));
        Assert.IsType<ConnectionStorage>(await kernel.Bind<StorageV1>(new BrainId(Brain), Pc, "files", Ct));
        var legacy = await kernel.Bind<ILegacyStorage>(new BrainId(Brain), Legacy, "files", Ct);
        Assert.Empty(await legacy.Roots(Ct));
    }

    [Fact]
    public void TheKernelsModulesPassCapabilityValidationAndAnUnknownMajorIsRefused()
    {
        var modules = CapabilityKitCoverageFacts.KernelModules();
        ModuleCapabilityValidator.Validate(Registry(), modules);

        var error = Assert.Throws<InvalidOperationException>(() => ModuleCapabilityValidator.Validate(Registry(), [.. modules, typeof(StorageTwoModule)]));
        Assert.Contains("Module 'storage-two' provides unknown capability storage@2.", error.Message, StringComparison.Ordinal);
        // A module whose neuron implements only the 0.6 contract passes through the os binding, and is refused without it.
        ModuleCapabilityValidator.Validate(Registry(), [typeof(LegacyOnlyNeurons)]);
        Assert.Throws<InvalidOperationException>(() => ModuleCapabilityValidator.Validate(new CapabilityRegistry(), [typeof(LegacyOnlyNeurons)]));
        // Windows and Drive implement storage@1 themselves (05-08, 05-09): they pass without it.
        ModuleCapabilityValidator.Validate(new CapabilityRegistry(), modules);
    }

    private static CapabilityRegistry Registry()
    {
        var registry = new CapabilityRegistry();
        KernelCapabilities.Configure(registry);
        return registry;
    }

    private static KernelCapabilityConnections Kernel(FakeRequests requests)
        => (KernelCapabilityConnections)Services(requests).GetRequiredService<ICapabilityConnections>();

    private static ServiceProvider Services(FakeRequests requests)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<ConnectorDefinition>>([DriveModule.Connector, new ConnectorDefinition("windows", "Windows", ["device"], []),
            new ConnectorDefinition(LegacyOnlyNeurons.Connector, "Legacy", ["api-key"], [])]);
        services.AddSingleton(new ConnectionNeuronTypes([typeof(DriveNeurons), typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsNeurons), typeof(LegacyOnlyNeurons)]));
        services.AddSingleton(DispatchGrains.Create(requests, new FakeLegacyStorage()));
        services.AddSingleton<ICapabilityConnections, KernelCapabilityConnections>();
        return services.BuildServiceProvider();
    }

    [DigitalBrain.Module("storage-two")]
    private sealed class StorageTwoModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([CapabilityId.Parse("storage@2")], [], [], [], []);
    }

    private class DispatchGrains : DispatchProxy
    {
        private FakeRequests _requests = null!;
        private FakeLegacyStorage _legacy = null!;
        private readonly INeuronHostCatalog _catalog = new Catalog();

        public static IGrainFactory Create(FakeRequests requests, FakeLegacyStorage legacy)
        {
            var proxy = Create<IGrainFactory, DispatchGrains>();
            var grains = (DispatchGrains)(object)proxy;
            grains._requests = requests;
            grains._legacy = legacy;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var grainType = targetMethod!.IsGenericMethod ? targetMethod.GetGenericArguments()[0] : args![0] as Type
                ?? throw new NotSupportedException(targetMethod.Name);
            if (grainType == typeof(INeuronHostCatalog)) { return _catalog; }
            if (grainType == typeof(INeuronHost)) { return new DispatchHost(Target(args)); }
            throw new NotSupportedException($"{targetMethod.Name} {grainType.Name}");
        }

        private object Target(object?[]? args)
        {
            var key = args is null ? "" : string.Join(' ', args.Select(arg => arg?.ToString()));
            return key.Contains("integrations.accounts", StringComparison.Ordinal) ? _requests : _legacy;
        }

        private sealed class Catalog : INeuronHostCatalog
        {
            public Task<string?> HostType(string neuronType) => Task.FromResult<string?>("neuron.host");
        }

        private sealed class DispatchHost(object target) : INeuronHost
        {
            public Task<bool> IsInterleaved(RuntimeInvocation call) => Task.FromResult(false);
            public Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult(Run(call));
            public Task<JsonElement?> InvokeInterleaved(RuntimeInvocation call, CancellationToken cancellationToken = default) => Invoke(call, cancellationToken);
            public Task Post(RuntimeInvocation call) => Task.CompletedTask;
            public Task PostInterleaved(RuntimeInvocation call) => Task.CompletedTask;
            public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            { await Task.CompletedTask; yield break; }

            private JsonElement? Run(RuntimeInvocation call)
            {
                var method = target.GetType().GetMethods().First(candidate => candidate.Name == call.Method
                    && candidate.GetParameters().Count(parameter => parameter.ParameterType != typeof(CancellationToken)) == call.Arguments.Length);
                var parameters = method.GetParameters();
                var values = new object?[parameters.Length];
                for (int index = 0, argument = 0; index < parameters.Length; index++)
                {
                    values[index] = parameters[index].ParameterType == typeof(CancellationToken)
                        ? CancellationToken.None
                        : call.Arguments[argument++].Deserialize(parameters[index].ParameterType, RuntimeProtocol.Json);
                }
                var returned = method.Invoke(target, values);
                if (returned is not Task task) { return returned is null ? null : JsonSerializer.SerializeToElement(returned, method.ReturnType, RuntimeProtocol.Json); }
                task.GetAwaiter().GetResult();
                if (!method.ReturnType.IsGenericType) { return null; }
                var result = task.GetType().GetProperty("Result")!.GetValue(task);
                return result is null ? null : JsonSerializer.SerializeToElement(result, method.ReturnType.GetGenericArguments()[0], RuntimeProtocol.Json);
            }
        }
    }

    private sealed class FakeUploads : DigitalBrain.Capabilities.Storage.IStorageUpload
    {
        public Task<string> BeginUpload(DigitalBrain.Capabilities.Storage.WritePlan plan, CancellationToken ct) => throw new NotSupportedException();
        public Task AppendUpload(string upload, long offset, byte[] bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<DigitalBrain.Capabilities.Storage.ItemId> CommitUpload(string upload, long length, CancellationToken ct) => throw new NotSupportedException();
        public Task AbortUpload(string upload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeLegacyStorage : ILegacyStorage
    {
        public Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([]);
        public Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([]);
        public Task<string> Read(string location, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([]);
    }

    private sealed class FakeRequests : IConnectionRequests
    {
        public ConnectionSummary[] Connections { get; set; } = [];
        public ConnectorKind[] Kinds { get; set; } = [];
        public ConnectionRef[] Pending { get; set; } = [];
        public List<string> Requested { get; } = [];

        public Task<ConnectorKind[]> ListKinds() => Task.FromResult(Kinds);
        public Task<ConnectionRef[]> ListPending() => Task.FromResult(Pending);
        public Task<ConnectionSummary[]> ListConnections() => Task.FromResult(Connections);
        public Task Reorder(string[] keys) => Task.CompletedTask;
        public Task<ConnectorOffer[]> ListOffers() => Task.FromResult<ConnectorOffer[]>([]);

        public Task<ConnectionRef> Request(string kind)
        {
            Requested.Add(kind);
            return Task.FromResult(new ConnectionRef(kind, "r" + Requested.Count));
        }
    }
}

// A connector whose neuron implements only the 0.6 storage contract (bound through LegacyStorageAdapter).
[DigitalBrain.Module(Connector)]
public sealed class LegacyOnlyNeurons : DigitalBrain.Module
{
    public const string Connector = "legacy-store";

    public static DigitalBrain.ModuleManifest Manifest { get; } = new([Capability.Storage.V1], [], [], [], []);
}

public sealed class LegacyOnlyState : ConnectionState;

[DigitalBrain.Neuron(LegacyOnlyNeurons.Connector, State = LegacyOnlyNeurons.Connector)]
internal sealed class LegacyOnlyNeuron : ConnectionNeuron<LegacyOnlyState>, ILegacyStorage
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) => Task.FromResult(ConnectionProbeResult.Connected());
    public Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([]);
    public Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([]);
    public Task<string> Read(string location, CancellationToken cancellationToken = default) => Task.FromResult("");
    public Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([]);
}
