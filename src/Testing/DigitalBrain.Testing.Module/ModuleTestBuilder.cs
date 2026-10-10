using DigitalBrain;
using DigitalBrain.Client;
using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using ModuleContract = DigitalBrain.Framework.Modules.Module;

namespace DigitalBrain.Testing.Module;

public sealed class ModuleTestBuilder
{
    private readonly BrainCompositionBuilder _composition = new();
    private ModuleOptions _options = new();
    private FakeModelServer? _model;
    private bool _started;

    public ModuleTestBuilder WithModule<TModule>(Action<ModuleConfiguration<TModule>>? configure = null)
        where TModule : ModuleContract, new()
    {
        EnsureMutable();
        _composition.WithModule(configure);
        return this;
    }
    public ModuleTestBuilder WithModule<TModule, TOptions>(Action<TOptions>? configureOptions = null,
        Action<ModuleConfiguration<TModule>>? configure = null)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        EnsureMutable();
        _composition.WithModule(configureOptions, configure);
        return this;
    }
    public ModuleTestBuilder RequireModules(IEnumerable<ModuleContract> modules)
    {
        EnsureMutable();
        _composition.RequireModules(modules);
        return this;
    }
    public ModuleTestBuilder ConfigureModule<TModule>(Action<ModuleConfiguration<TModule>> configure)
        where TModule : ModuleContract, new()
    {
        EnsureMutable();
        _composition.ConfigureModule(configure);
        return this;
    }
    public ModuleTestBuilder ConfigureModule<TModule, TOptions>(Action<TOptions> configureOptions)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        EnsureMutable();
        _composition.ConfigureModule<TModule, TOptions>(configureOptions);
        return this;
    }
    public ModuleTestBuilder WithExecution(TestExecutionOptions execution)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(execution);
        _options = _options with { Execution = execution };
        return this;
    }
    // The OpenAI integration points at the fake: every model call runs the real adapter against it.
    public ModuleTestBuilder WithFakeModel(FakeModelServer model)
    {
        EnsureMutable();
        _model = model ?? throw new ArgumentNullException(nameof(model));
        return this;
    }
    public ModuleTestBuilder ConfigureSilo(Action<ISiloBuilder> configure)
    {
        EnsureMutable();
        _options = _options with { ConfigureSilo = _options.ConfigureSilo + configure };
        return this;
    }
    public ModuleTestBuilder ConfigureClient(Action<IClientBuilder> configure)
    {
        EnsureMutable();
        _options = _options with { ConfigureClient = _options.ConfigureClient + configure };
        return this;
    }
    // Short leases and renewals so subscription expiry and reactivation show up within a test's timeout.
    public ModuleTestBuilder WithFastSubscriptions(int bufferCapacity = 256)
    {
        static void Shorten(SubscriptionOptions options)
        {
            options.RenewEvery = TimeSpan.FromMilliseconds(200);
            options.OperationTimeout = TimeSpan.FromMilliseconds(500);
        }
        return ConfigureSilo(silo => silo.Services.Configure<ObserverOptions>(options => options.Lease = TimeSpan.FromSeconds(2)))
            .ConfigureClient(client => client.Services.Configure<SubscriptionOptions>(options => { Shorten(options); options.BufferCapacity = bufferCapacity; }));
    }
    // Hosts the composed modules' HTTP endpoints through the product pipeline on a loopback
    // port; the brain then answers HTTP through ModuleBrain.HttpClient.
    public ModuleTestBuilder WithHttpEdge()
    {
        EnsureMutable();
        _options = _options with { HttpEdge = true };
        return this;
    }
    // The cluster storage is the machine's part log over an in-process mesh of 8 platform-owned nodes (4+2,
    // 2 manifest replicas); platform storage outlives a silo restart; the default brain is provisioned so neurons inside it have an owning account.
    public ModuleTestBuilder WithPartLog()
    {
        EnsureMutable();
        _options = _options with { PartLog = true };
        var platform = new System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] State, long ETag)>(StringComparer.Ordinal);
        return ConfigureSilo(silo =>
        {
            foreach (var memory in silo.Services.Where(d => d.ServiceType == typeof(Orleans.Storage.IGrainStorage)
                && Equals(d.ServiceKey, DigitalBrainNames.PlatformGrainStorage)).ToArray()) { silo.Services.Remove(memory); }
            silo.Services.AddKeyedSingleton<Orleans.Storage.IGrainStorage>(DigitalBrainNames.PlatformGrainStorage, (provider, _) =>
                new SessionGrainStorage(platform, provider.GetRequiredService<Orleans.Serialization.Serializer>()));
            silo.Services.Configure<DigitalBrain.Platform.Mesh.MeshOptions>(mesh => { mesh.DataShards = 4; mesh.ParityShards = 2; mesh.ManifestReplicas = 2; });
            DigitalBrain.Platform.Parts.MachineHosting.AddMachinePartLog(silo);
        });
    }
    // This host is a machine of a separate trust cluster (H2, R34): it reaches the trust grains only through its
    // private gateway client and pairs with the trust host's other machines through its pairing secret (R52).
    public ModuleTestBuilder WithTrust(TrustHost trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        EnsureMutable();
        var key = DigitalBrain.Platform.Trust.TrustOptions.Key;
        _options = _options with
        {
            Execution = _options.Execution with
            {
                PrivateConfiguration = new Dictionary<string, string?>(_options.Execution.PrivateConfiguration)
                {
                    [$"{key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.Gateway)}"] = $"127.0.0.1:{trust.GatewayPort}",
                    [$"{key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.ClusterId)}"] = trust.ClusterId,
                    [$"{key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.ServiceId)}"] = trust.ServiceId,
                    [$"{DigitalBrain.Platform.Parts.PartsOptions.Key}:{nameof(DigitalBrain.Platform.Parts.PartsOptions.PairingSecret)}"] = trust.PairingSecret,
                },
            },
        };
        return this;
    }
    public ModuleTestBuilder WithReminders()
    {
        EnsureMutable();
        _options = _options with { UseReminders = true };
        return this;
    }
    public Task<ModuleBrain> StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        var composition = _composition.Build();
        _started = true;
        var options = _model is null ? _options : _options with
        {
            Execution = _options.Execution with
            {
                PrivateConfiguration = new Dictionary<string, string?>(_options.Execution.PrivateConfiguration)
                {
                    [IntegrationSchema.Key("openai", "ApiKey")] = "fake-model-key",
                    [IntegrationSchema.Key("openai", "Endpoint")] = _model.Endpoint.AbsoluteUri,
                },
            },
        };
        return ModuleTest.StartAsync(options with
        {
            Modules = composition.Modules,
            ConfigureSilo = silo => { composition.ConfigureLocalServices(silo.Services); _options.ConfigureSilo?.Invoke(silo); },
        }, cancellationToken);
    }
    private void EnsureMutable()
    {
        if (_started) { throw new InvalidOperationException("A test builder starts one session. Create another builder for a new session."); }
    }
}
