using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Testing.Aspire.Diagnostics;

namespace DigitalBrain.Testing.E2E;

public sealed class E2ETestBuilder<TAppHost> where TAppHost : class
{
    // A configured module replaces the AppHost's code-declared options with fixture options.
    // Repeated edits compose on the same options, including empty collections and false values.
    private readonly Dictionary<Type, object> _moduleOptions = [];
    private readonly Dictionary<string, string?> _optionOverrides = new(StringComparer.OrdinalIgnoreCase);
    private TestExecutionOptions _execution = new();
    private BrowserOptions _browser = new() { Headless = true };
    private bool _started;

    public E2ETestBuilder<TAppHost> ConfigureModule<TModule, TOptions>(Action<TOptions> configureOptions)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configureOptions);
        using var scope = BrowserConfiguration.Begin(_browser);
        var edited = _moduleOptions.TryGetValue(typeof(TModule), out var existing) ? (TOptions)existing : new TOptions();
        configureOptions(edited);
        _browser = scope.Options;
        edited.Validate();
        _moduleOptions[typeof(TModule)] = edited;
        var id = new TModule().Id.ToString();
        var prefix = DigitalBrainConfiguration.ModuleSection(id) + ":";
        foreach (var key in _optionOverrides.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        { _optionOverrides.Remove(key); }
        foreach (var (key, value) in ModuleOptionsProjection.Overrides<TModule, TOptions>(edited)) { _optionOverrides[key] = value; }
        return this;
    }

    // A startup budget for hosts whose resources compile at start (a CI runner building the
    // web shell needs more than the default); merges into the composition's execution options.
    public E2ETestBuilder<TAppHost> WithStartupTimeout(TimeSpan timeout)
    {
        EnsureMutable();
        TestExecutionOptions.ValidateTimeout(timeout);
        _execution = _execution with { StartupTimeout = timeout };
        return this;
    }

    public E2ETestBuilder<TAppHost> WithExecution(TestExecutionOptions execution)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(execution);
        _execution = execution;
        return this;
    }

    // The collector plugs into the AppHost's own OTLP setting; Aspire projects it to every resource.
    public E2ETestBuilder<TAppHost> ExportingTelemetryTo(TestTelemetryCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        return WithHostConfiguration("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", collector.Endpoint.AbsoluteUri)
            .WithHostConfiguration("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "");
    }

    public E2ETestBuilder<TAppHost> InEnvironment(string environmentName)
        => WithHostConfiguration(DigitalBrainHostingOptions.EnvironmentKey, environmentName);

    // Hosting-layer configuration for the AppHost builder itself (not a resource), such as
    // DigitalBrain:Modules:flutter:Hosting:WebContainer.
    public E2ETestBuilder<TAppHost> WithHostConfiguration(string key, string? value)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var merged = new Dictionary<string, string?>(_execution.HostConfiguration, StringComparer.OrdinalIgnoreCase) { [key] = value };
        _execution = _execution with { HostConfiguration = merged };
        return this;
    }

    public E2ETestBuilder<TAppHost> WithBrowser(BrowserOptions browser)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(browser);
        _browser = browser;
        return this;
    }

    public Task<E2EBrain> StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        _started = true;
        return E2ETest.StartAsync<TAppHost>(_optionOverrides, _execution, _browser, cancellationToken);
    }

    internal IReadOnlyDictionary<string, string?> OptionOverrides => _optionOverrides;
    internal BrowserOptions BrowserOptions => _browser;

    private void EnsureMutable()
    {
        if (_started) { throw new InvalidOperationException("A test builder starts one session. Create another builder for a new session."); }
    }
}
