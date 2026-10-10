using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Model;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using ModuleContract = DigitalBrain.Framework.Modules.Module;

namespace DigitalBrain.Testing;

public sealed class TestBrain
{
    private readonly BrainCompositionBuilder _composition = new();
    private readonly Dictionary<string, string?> _configuration = new(StringComparer.OrdinalIgnoreCase);
    private Action<IServiceCollection>? _services;
    private Action<ISiloBuilder>? _silo;
    private TestExecutionOptions _execution = new();
    private ITestTarget _target = new InProcessTarget();
    private bool _started;

    private TestBrain() { }

    public static TestBrain Create() => new();

    public TestBrain WithModule<TModule>(Action<ModuleConfiguration<TModule>>? configure = null) where TModule : ModuleContract, new()
    {
        EnsureMutable();
        _composition.WithModule(configure);
        return this;
    }

    public TestBrain WithModule<TModule, TOptions>(Action<TOptions>? options = null, Action<ModuleConfiguration<TModule>>? configure = null)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        EnsureMutable();
        _composition.WithModule(options, configure);
        return this;
    }

    public TestBrain WithOptions<TModule, TOptions>(Action<TOptions> options)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        EnsureMutable();
        _composition.ConfigureModule<TModule, TOptions>(options);
        return this;
    }

    public TestBrain WithIntegration(IntegrationSchema schema, string field, string value)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return WithConfiguration(schema.Key(field), value);
    }

    public TestBrain WithIntegration(IntegrationSchema schema, string field, Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WithIntegration(schema, field, value.AbsoluteUri);
    }

    // The OpenAI integration points at the fake: every model call runs the real adapter against it.
    public TestBrain WithFakeModel(FakeModelServer model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return WithConfiguration(IntegrationSchema.Key("openai", "ApiKey"), "fake-model-key")
            .WithConfiguration(IntegrationSchema.Key("openai", "Endpoint"), model.Endpoint.AbsoluteUri);
    }

    // Private configuration: host-owned keys (Orleans, modules, connection strings) are refused.
    public TestBrain WithConfiguration(string key, string? value)
    {
        EnsureMutable();
        new TestExecutionOptions { PrivateConfiguration = new Dictionary<string, string?> { [key] = value } }.Validate();
        _configuration[key] = value;
        return this;
    }

    // In-process escape hatch for seams that have no integration boundary (time, sandbox, flaky storage).
    public TestBrain ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureMutable();
        _services += configure;
        return this;
    }

    public TestBrain WithExecution(TestExecutionOptions execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        EnsureMutable();
        execution.Validate();
        _execution = execution;
        return this;
    }

    // In-process brains always run memory reminders; deleted once callers stop asking (Task 20).
    public TestBrain WithReminders()
    {
        EnsureMutable();
        return this;
    }

    // Short leases and renewals so subscription expiry and reactivation show up within a test's timeout.
    public TestBrain WithFastSubscriptions(int bufferCapacity = 256)
        => ConfigureServices(services =>
        {
            services.Configure<ObserverOptions>(options => options.Lease = TimeSpan.FromSeconds(2));
            services.Configure<SubscriptionOptions>(options =>
            {
                options.RenewEvery = TimeSpan.FromMilliseconds(200);
                options.OperationTimeout = TimeSpan.FromMilliseconds(500);
                options.BufferCapacity = bufferCapacity;
            });
        });

    // Sweep-period shims over the one host (deleted in Task 20).
    // HTTP is always on: every in-process brain serves its edge.
    public TestBrain WithHttpEdge()
    {
        EnsureMutable();
        return this;
    }

    // The brain runs as the owner's machine: cluster storage on the part log over an in-process mesh of 8
    // platform-owned nodes (4+2, 2 manifest replicas), the default brain provisioned at start.
    public TestBrain WithPartLog()
        => WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Own))
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}", "8")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ManifestReplicas)}", "2");

    // The silo and the client share the host's composed silo, configured after the server's own registrations.
    public TestBrain ConfigureSilo(Action<ISiloBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureMutable();
        _silo += configure;
        return this;
    }

    public TestBrain ConfigureClient(Action<IClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return ConfigureSilo(silo => configure(new ClientOverSilo(silo)));
    }

    private sealed class ClientOverSilo(ISiloBuilder silo) : IClientBuilder
    {
        public IServiceCollection Services => silo.Services;
        public IConfiguration Configuration => silo.Configuration;
    }

    public TestBrain On(ITestTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureMutable();
        _target = target;
        return this;
    }

    public async Task<ITestBrain> StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        _started = true;
        if (_silo is not null && _target is not InProcessTarget)
        { throw new NotSupportedException("ConfigureSilo and ConfigureClient reach the in-process host only."); }
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        _execution.Validate();
        var composition = _composition.Build();
        Dictionary<string, string?> configuration = new(_execution.PrivateConfiguration, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _configuration) { configuration[key] = value; }
        var services = composition.RequiresLocalServices ? composition.ConfigureLocalServices + _services : _services;
        var plan = new TestBrainPlan(composition.Modules, configuration, services, _execution, Client: null, Silo: _silo);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_execution.StartupTimeout);
        return await _target.StartAsync(plan, deadline.Token).ConfigureAwait(false);
    }

    private void EnsureMutable()
    {
        if (_started) { throw new InvalidOperationException("A test builder starts one session. Create another builder for a new session."); }
    }
}
