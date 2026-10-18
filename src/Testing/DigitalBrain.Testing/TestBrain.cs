using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using ModuleContract = DigitalBrain.Framework.Modules.Module;

namespace DigitalBrain.Testing;

public sealed class TestBrain
{
    private readonly BrainCompositionBuilder _composition = new();
    private readonly Dictionary<string, string?> _configuration = new(StringComparer.OrdinalIgnoreCase);
    private Action<IServiceCollection>? _services;
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

    // A composition named as a list (the OS catalog): modules already declared keep their configuration.
    internal TestBrain WithModules(IEnumerable<ModuleContract> modules)
    {
        EnsureMutable();
        _composition.RequireModules(modules);
        return this;
    }

    public TestBrain WithOptions<TModule, TOptions>(Action<TOptions> options)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        EnsureMutable();
        // A declared module carries its options; an undeclared one belongs to the target's host, which replaces its own.
        if (_composition.Declares<TModule>()) { _composition.ConfigureModule<TModule, TOptions>(options); }
        else { _composition.ProjectOptions<TModule, TOptions>(options); }
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

    // In-process escape hatch for seams that have no integration boundary (time, sandbox, flaky storage). It runs after the
    // server's and Orleans' registrations, so it replaces or decorates theirs; the kernel wraps the cluster storage after it.
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

    public TestBrain ConfigureExecution(Func<TestExecutionOptions, TestExecutionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureMutable();
        return WithExecution(configure(_execution));
    }

    // The AppHost builder's own configuration on an Aspire target (hosting-layer knobs, never integration values).
    public TestBrain WithHostConfiguration(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureMutable();
        _execution = _execution with
        {
            HostConfiguration = new Dictionary<string, string?>(_execution.HostConfiguration, StringComparer.OrdinalIgnoreCase) { [key] = value },
        };
        return this;
    }

    public TestBrain WithStartupTimeout(TimeSpan timeout)
    {
        TestExecutionOptions.ValidateTimeout(timeout);
        EnsureMutable();
        _execution = _execution with { StartupTimeout = timeout };
        return this;
    }

    // An in-process host with no HTTP edge: the silo and services are the product's, ITestBrain.HttpClient throws.
    public TestBrain WithoutHttp()
    {
        EnsureMutable();
        _execution = _execution with { Http = false };
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
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        _execution.Validate();
        var composition = _composition.Build();
        Dictionary<string, string?> configuration = new(_execution.PrivateConfiguration, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _configuration) { configuration[key] = value; }
        foreach (var (key, value) in composition.OptionOverrides) { configuration[key] = value; }
        var services = composition.RequiresLocalServices ? composition.ConfigureLocalServices + _services : _services;
        var plan = new TestBrainPlan(composition.Modules, configuration, services, _execution, Client: null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_execution.StartupTimeout);
        return await _target.StartAsync(plan, deadline.Token).ConfigureAwait(false);
    }

    private void EnsureMutable()
    {
        if (_started) { throw new InvalidOperationException("A test builder starts one session. Create another builder for a new session."); }
    }
}
