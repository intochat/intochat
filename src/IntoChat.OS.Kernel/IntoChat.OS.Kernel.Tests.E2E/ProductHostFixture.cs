using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Operations;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Authority;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition;
using DigitalBrain.Runtime.Orleans.Confirmations;
using IntoChat.OS.Kernel.Tests.E2E.Policies;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

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
            typeof(BddVerified).Assembly,
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
            typeof(FlameModule).Assembly,
        }.Distinct().ToArray();

        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                services.AddSingleton<ICapabilityConnections>(_connections);
                services.TryAddSingleton(TimeProvider.System);
                services.AddOptions<ConfirmationOptions>();
                services.TryAddSingleton<IConfirmationContexts, KernelConfirmations>();
                services.TryAddSingleton<DigitalBrain.Runtime.Orleans.Enforcement.ICallFilter, DigitalBrain.Runtime.Orleans.Enforcement.CallFilter>();
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, DigitalBrain.Runtime.Orleans.Enforcement.PlainNeuronCallFilter>());
                services.TryAddSingleton<IDeepCopier<IntoChat.OS.Modules.AI.Context.ContextSlice>>(new ShallowCopier<IntoChat.OS.Modules.AI.Context.ContextSlice>());
                services.AddNeuronProxies();
                services.AddSingleton<IPolicyModel>(_policyModel);
                services.TryAddSingleton<PolicyWriter>();
            });

        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(ProductHostTestModule),
                    typeof(ChargesTestModule),
                    typeof(AppsModule),
                    typeof(AuthorityPlainNeurons),
                    typeof(AIModule),
                    typeof(AspireModule),
                    typeof(FlutterModule),
                    typeof(FlameModule),
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

        var app = builder.Build();
        app.UseRouting();
        app.Use(async (http, next) =>
        {
            if (http.Request.RouteValues.TryGetValue("brainId", out var raw) && raw is string { Length: > 0 } brainId)
            {
                RequestContext.Set(KernelInvocationSink.CallerKey, "");
                // The person by default; a request carrying the test app header arrives as that app through the app proxy.
                var caller = http.Request.Headers.TryGetValue(AppCallerHeader, out var app) && app.ToString() is { Length: > 0 } appKey
                    ? CapabilityCallers.Of(brainId, brainId, brainId, CallerKind.App, appKey)
                    : CapabilityCallers.Of(brainId, brainId, brainId);
                using (RuntimeCallerContext.Stamp(caller))
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

    public TApp AttachSilo<TApp>(TApp app, string brain) where TApp : App
    {
        ArgumentNullException.ThrowIfNull(app);
        app.AttachNeuronResolver((contractType, neuronKey) =>
        {
            var fullKey = neuronKey.Contains('/', StringComparison.Ordinal)
                && string.Equals(BrainKey.BrainOf(neuronKey), brain, StringComparison.Ordinal)
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
