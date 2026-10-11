using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class CustodyOptionsFacts
{
    private static readonly string CustodyKey = $"{PartsOptions.Key}:{nameof(PartsOptions.Custody)}";

    // R78: Tpm is the production default for a single-machine owner, so a production own machine refuses Configured.
    // The dev AppHost keeps Configured (one shared custody root, R74) because multi-machine owners under Tpm need part
    // keys wrapped to each machine's TPM key (key sharing, later); a Cloud machine may run Configured.
    [Fact(Timeout = 120_000)]
    public async Task ProductionRefusesConfiguredCustodyOnAnOwnMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var brain = await TestBrain.Create().AsMachine()
                .WithConfiguration(CustodyKey, nameof(MasterKeyCustodyKind.Configured))
                .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new ProductionEnvironment())))
                .StartAsync(ct);
        });
        Assert.Contains(CustodyKey, Describe(refused), StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), Describe(refused), StringComparison.Ordinal);
    }

    // R81: an operator's explicit acknowledgement lets a production own machine start on a configured root, with a warning.
    [Fact(Timeout = 120_000)]
    public async Task AProductionOwnMachineStartsWithAcknowledgedConfiguredCustody()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new WarningLog();
        await using var brain = await TestBrain.Create().AsMachine()
            .WithConfiguration(CustodyKey, nameof(MasterKeyCustodyKind.Configured))
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.ConfiguredCustodyAcknowledged)}", "true")
            .ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new ProductionEnvironment()));
                services.AddSingleton<ILoggerProvider>(log);
            })
            .StartAsync(ct);
        Assert.True(brain.Services.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.Contains(log.Lines, line => line.Contains("the custody root is a configured secret, not hardware", StringComparison.Ordinal));
    }

    // R78: a Cloud machine holds only non-private parts (Q24), so it may run Configured custody in Production.
    [Fact(Timeout = 120_000)]
    public async Task AProductionCloudMachineStartsWithConfiguredCustody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine()
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Cloud))
            .WithConfiguration(CustodyKey, nameof(MasterKeyCustodyKind.Configured))
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new ProductionEnvironment())))
            .StartAsync(ct);
        Assert.True(brain.Services.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.Equal(MachineRole.Cloud, brain.Services.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<PartsOptions>>().Value.Role);
    }

    [Fact(Timeout = 120_000)]
    public async Task TpmCustodyWithoutAProviderIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var brain = await TestBrain.Create().AsMachine()
                .WithConfiguration(CustodyKey, nameof(MasterKeyCustodyKind.Tpm))
                .StartAsync(ct);
        });
        Assert.Contains(CustodyKey, Describe(refused), StringComparison.Ordinal);
    }

    [Fact]
    public void CustodyDefaultsToConfigured() => Assert.Equal(MasterKeyCustodyKind.Configured, new PartsOptions().Custody);

    // The refusal may surface wrapped by the host; what matters is the chain names the key.
    private static string Describe(Exception exception) => exception.ToString();

    private sealed class WarningLog : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(WarningLog log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { if (IsEnabled(logLevel)) { log.Lines.Enqueue(formatter(state, exception)); } }
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DigitalBrain.Platform.Tests.Unit";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
