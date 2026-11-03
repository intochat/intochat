using System.Reflection;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Authority;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Integrations;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Confirmations;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Serialization.Cloning;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Kernel.Tests.E2E;

public sealed class ProductHostFixture : IAsyncLifetime
{
    private readonly FakeStorage _storage = new("Workspace Disk");
    private readonly SingleStorageConnections _connections;
    private IInProcessHost? _host;
    private int _counter;

    public ProductHostFixture()
    {
        _storage.AddFolder(_storage.Root, "projects");
        _connections = new SingleStorageConnections("local-disk", "Workspace Disk", _storage);
    }

    public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

    public IReadOnlyList<(BrainId Brain, string ConnectionKey, string CallerApp)> BoundStorageCalls => _connections.BoundCalls;

    public string NewBrain(string prefix)
        => $"{prefix}{Interlocked.Increment(ref _counter)}";

    public async ValueTask InitializeAsync()
    {
        var signalAssemblies = new[]
        {
            typeof(ProductHostTestModule).Assembly,
            typeof(BddVerified).Assembly,
            typeof(AppsModule).Assembly,
            typeof(IApp).Assembly,
            typeof(AuthorityPlainNeurons).Assembly,
            typeof(IConnectionRequests).Assembly,
            typeof(AIModule).Assembly,
            typeof(IMemory).Assembly,
            typeof(AspireModule).Assembly,
            typeof(IAspire).Assembly,
            typeof(FlutterModule).Assembly,
            typeof(IView).Assembly,
            typeof(FlameModule).Assembly,
            typeof(ConfirmationNeurons).Assembly,
        }.Distinct().ToArray();

        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                services.AddSingleton<ICapabilityConnections>(_connections);
                services.TryAddSingleton<ICallFilter, CallFilter>();
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, PlainNeuronCallFilter>());
                services.TryAddSingleton<IDeepCopier<IntoChat.OS.Modules.AI.Context.ContextSlice>>(new ShallowCopier<IntoChat.OS.Modules.AI.Context.ContextSlice>());
            });

        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(ProductHostTestModule),
                    typeof(AppsModule),
                    typeof(AuthorityPlainNeurons),
                    typeof(AIModule),
                    typeof(AspireModule),
                    typeof(FlutterModule),
                    typeof(FlameModule),
                    typeof(ConfirmationNeurons),
                ],
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    public TApp AttachSilo<TApp>(TApp app, string brain) where TApp : App
    {
        ArgumentNullException.ThrowIfNull(app);
        app.AttachNeuronResolver((contractType, neuronKey) =>
        {
            var fullKey = neuronKey.Contains('/', StringComparison.Ordinal)
                && BrainId.IsValid(neuronKey[..neuronKey.IndexOf('/', StringComparison.Ordinal)])
                ? neuronKey
                : $"{brain}/{neuronKey}";

            if (contractType.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false) is not null)
            {
                return SiloNeurons.Get(Services, contractType, fullKey);
            }

            var alias = contractType.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
                ?? throw new InvalidOperationException($"Contract {contractType.Name} has neither DefaultNeuronType nor NeuronAlias.");
            var sep = fullKey.IndexOf('/', StringComparison.Ordinal);
            var id = new NeuronId(new BrainId(fullKey[..sep]), alias, fullKey[(sep + 1)..]);
            return SiloNeurons.Get(Services, contractType, id);
        });
        return app;
    }

    public async Task PublishAppViewToSiloAsync(string brain, string appName, View? side, View main)
    {
        ArgumentNullException.ThrowIfNull(main);
        var appKey = IApp.Key(brain, appName);
        if (side is not null)
        {
            var sideNeuron = SiloNeurons.Get<IView>(Services, appKey + "/side");
            await sideNeuron.Publish(new UiView(1, 1, Ui.Column("view", [Ui.Text("title", side.Title)])));
        }
        var mainNeuron = SiloNeurons.Get<IView>(Services, appKey + "/view");
        await mainNeuron.Publish(new UiView(1, 1, Ui.Column("view", [Ui.Text("title", main.Title)])));
    }

    [DigitalBrain.Module("product.host.e2e")]
    private sealed class ProductHostTestModule : DigitalBrain.Module
    {
        public static ModuleManifest Manifest { get; } = new(
            Provides: [],
            Requires: [],
            Needs: [],
            Settings: [],
            Secrets: []);
    }

    private sealed class SingleStorageConnections(string key, string label, IStorage storage) : ICapabilityConnections
    {
        private readonly List<(BrainId Brain, string ConnectionKey, string CallerApp)> _boundCalls = [];

        public IReadOnlyList<(BrainId Brain, string ConnectionKey, string CallerApp)> BoundCalls => _boundCalls;

        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>(
                [new BrainConnection(key, label, true, [Capability.Storage.V1], null)]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
        {
            _boundCalls.Add((brain, connectionKey, callerApp));
            return Task.FromResult((TCapability)(object)storage);
        }

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
