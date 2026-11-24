using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Hosting;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Kernel.Tests.E2E.Policies;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.Aspire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;
using IntoChat.OS.Modules.Identity;

namespace IntoChat.OS.Kernel.Tests.E2E;

public sealed class ProductHostFixture : IAsyncLifetime
{
    private readonly FakeStorage _storage = new("Workspace Disk");
    private readonly SingleStorageConnections _connections;
    private readonly QueuedPolicyModel _policyModel = new();
    private IInProcessHost? _host;
    private WebApplication? _webApp;
    private HttpClient? _httpClient;
    private int _counter;

    public ProductHostFixture()
    {
        _storage.AddFolder(_storage.Root, "projects");
        _connections = new SingleStorageConnections("local-disk", "Workspace Disk", _storage);
    }

    public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

    public DigitalBrain.Access.EdgeAuthority Edge => Services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>();

    public HttpClient Http => _httpClient ?? throw new InvalidOperationException("HTTP host is not started.");

    /// <summary>A request header naming the app key a test request should be stamped as, instead of the person.</summary>
    public const string AppCallerHeader = "X-IntoChat-Test-App";

    public IReadOnlyList<(BrainId Brain, string ConnectionKey, string CallerApp)> BoundStorageCalls => _connections.BoundCalls;

    public string NewBrain(string prefix)
        => $"{prefix}{Interlocked.Increment(ref _counter)}";

    public void EnqueuePolicyReply(params string[] replies)
        => _policyModel.Enqueue(replies);

    public async ValueTask InitializeAsync()
    {
        var signalAssemblies = new[]
        {
            typeof(DigitalBrain.Signal).Assembly,
            typeof(ProductHostTestModule).Assembly,
            typeof(ChargesTestModule).Assembly,
            typeof(AppsModule).Assembly,
            typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly,
            typeof(IApp).Assembly,
            typeof(AuthorityPlainNeurons).Assembly,
            typeof(IConnectionRequests).Assembly,
            typeof(AIModule).Assembly,
            typeof(IMemory).Assembly,
            typeof(AspireModule).Assembly,
            typeof(IAspire).Assembly,
            typeof(FlutterModule).Assembly,
            typeof(IView).Assembly,
        }.Distinct().ToArray();

        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                services.AddSingleton<ICapabilityConnections>(_connections);
                services.TryAddSingleton(TimeProvider.System);
                services.AddNeuronProxies();
                services.AddSingleton<IPolicyModel>(_policyModel);
                services.TryAddSingleton<PolicyWriter>();
                services.TryAddSingleton<IMemoryEmbedder>(new HybridMemoryEmbedder());
            });

        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(ProductHostTestModule),
                    typeof(ChargesTestModule),
                    typeof(AppsModule),
                    typeof(IntoChat.OS.Modules.Runs.RunsModule),
                    typeof(AuthorityPlainNeurons),
                    typeof(AIModule),
                    typeof(AspireModule),
                    typeof(FlutterModule),
                ],
            },
            CancellationToken.None);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(ProductHostFixture).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IBrainAccess>(new AllowAllBrainAccess());
        builder.Services.AddSingleton(_host.Services.GetRequiredService<INeuronProxies>());
        builder.Services.AddSingleton(_host.Services.GetRequiredService<PolicyWriter>());
        builder.Services.AddSingleton<IPolicyModel>(_policyModel);
        builder.Services.AddSingleton(Edge);
        builder.Services.AddSingleton(_host.Services.GetRequiredService<DigitalBrain.Access.ICallerAmbient>());

        var app = builder.Build();
        app.UseRouting();
        app.Use(async (http, next) =>
        {
            if (http.Request.RouteValues.TryGetValue("brainId", out var raw) && raw is string { Length: > 0 } brainId)
            {
                RequestContext.Set(KernelInvocationSink.CallerKey, "");
                // The person by default; a request carrying the test app header arrives as that app through the app proxy.
                var person = new DigitalBrain.Access.Actor.Person(brainId, brainId);
                var caller = http.Request.Headers.TryGetValue(AppCallerHeader, out var app) && app.ToString() is { Length: > 0 } appKey
                    ? Edge.App(appKey, person, new BrainId(brainId))
                    : Edge.Person(brainId, brainId, new BrainId(brainId));
                using (Edge.Enter(caller))
                {
                    await next(http);
                }

                return;
            }

            await next(http);
        });
        PoliciesEndpoints.Map(app);
        await app.StartAsync();

        _webApp = app;
        _httpClient = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        if (_webApp is not null)
        {
            await _webApp.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    public TApp AttachSilo<TApp>(string brain) where TApp : class
        => AttachSilo(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<TApp>(Services), brain);

    public TApp AttachSilo<TApp>(TApp app, string brain) where TApp : class
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app is Neuron neuron)
        {
            var brainId = new BrainId(brain);
            var neuronType = typeof(TApp).Name.ToLowerInvariant();
            var id = new NeuronId(brainId, neuronType, neuronType);
            var context = new FakeNeuronContext(id, Services);

            // Only a neuron type the silo hosts answers an optional slot; the rest stay unbound, as on a runtime without them.
            void RegisterProxy<T>(string key, bool hosted = false) where T : class
            {
                try
                {
                    var type = PlainNeuronContracts.TypeOf(typeof(T))
                        ?? typeof(T).GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
                        ?? typeof(T).GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
                        ?? typeof(T).Name.ToLowerInvariant();
                    var neuronId = new NeuronId(brainId, type, key);
                    var proxy = SiloNeurons.Get<T>(Services, neuronId);
                    context.Proxies.Register(neuronId, proxy);
                    if (hosted) { context.RegisterClass(type, typeof(T)); }
                }
                catch
                {
                }
            }

            RegisterProxy<IntoChat.OS.Modules.Apps.IUserSettings>("settings", hosted: true);
            RegisterProxy<IntoChat.OS.Modules.Apps.IUserSettings>("default", hosted: true);
            RegisterProxy<IntoChat.OS.Modules.Apps.IProfile>("profile", hosted: true);
            RegisterProxy<IntoChat.OS.Modules.Apps.IProfile>("default", hosted: true);
            RegisterProxy<IntoChat.OS.Modules.Apps.IDesktopRecords>(IntoChat.OS.Modules.Apps.IDesktopRecords.Key);
            RegisterProxy<IntoChat.OS.Modules.Apps.IInoRecords>("apps/ino/records");
            RegisterProxy<IntoChat.OS.Modules.Apps.ISynapseStudio>("ino/synapses");
            RegisterProxy<IntoChat.OS.Modules.Apps.IData>("ino/data");
            RegisterProxy<IntoChat.OS.Modules.ArtCraft.IArtCraftStudio>("artcraft");
            RegisterProxy<IntoChat.OS.Modules.Apps.IArtCraftEngine>("apps/artcraft-engine");
            RegisterProxy<IntoChat.OS.Modules.Apps.IArtCraftEngine>("default");
            RegisterProxy<IntoChat.OS.Modules.AI.Agents.IAgent>("ino/conversations/default");
            RegisterProxy<IntoChat.OS.Modules.AI.Agents.IAgent>("ino/conversations/main");
            RegisterProxy<IntoChat.OS.Modules.AI.Agents.IAgent>("default");
            RegisterProxy<IAspire>("aspire");
            RegisterProxy<IAspire>("default");
            RegisterProxy<IMemory>("memory");
            RegisterProxy<IMemory>("default");

            var component = DigitalBrain.Components.ComponentRegistry.Of(typeof(TApp)) ?? new DigitalBrain.Components.ComponentId(neuronType);
            context.Bindings = DigitalBrain.Components.Slots.Resolve(brainId, component, DigitalBrain.Components.ComponentManifests.SlotsOf(typeof(TApp)), installed: null);
            NeuronAttachment.Attach(neuron, context);
            NeuronAttachment.Activate(neuron, CancellationToken.None).GetAwaiter().GetResult();
        }
        return app;
    }

    public async Task PublishAppViewToSiloAsync(string brain, string appName, View? side, View main)
    {
        ArgumentNullException.ThrowIfNull(main);
        var appKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brain), appName).Key;
        if (side is not null)
        {
            var sideNeuron = SiloNeurons.Get<IView>(Services, appKey + "/side");
            await sideNeuron.Publish(1, side);
        }
        var mainNeuron = SiloNeurons.Get<IView>(Services, appKey + "/view");
        await mainNeuron.Publish(1, main);
    }

    [DigitalBrain.Modules.Module("product.host.e2e")]
    private sealed class ProductHostTestModule : DigitalBrain.Modules.Module
    {
        public static ModuleManifest Manifest { get; } = new(
            Provides: [],
            Requires: [],
            Needs: [],
            Settings: [],
            Secrets: []);
    }

    private sealed class AllowAllBrainAccess : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(string.Equals(accountId, brainId, StringComparison.Ordinal));
    }

    private sealed class QueuedPolicyModel : IPolicyModel
    {
        private readonly ConcurrentQueue<string> _replies = new();

        public void Enqueue(params string[] replies)
        {
            ArgumentNullException.ThrowIfNull(replies);
            foreach (var reply in replies)
            {
                _replies.Enqueue(reply);
            }
        }

        public Task<string> Complete(string brainId, string system, string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_replies.TryDequeue(out var reply))
            {
                throw new InvalidOperationException("QueuedPolicyModel has no queued reply.");
            }

            return Task.FromResult(reply);
        }
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
