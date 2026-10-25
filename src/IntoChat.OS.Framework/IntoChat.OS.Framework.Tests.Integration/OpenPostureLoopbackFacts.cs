using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration;

// Open posture serves every anonymous caller as the owner. On IntoChatOS:Topology local (or local-cloud, or unset) on the host it
// may listen on loopback only; cloud and containers are out of scope. Cases (a)-(g) are the former run-mode cases, now driven by
// the topology with the same outcomes; (h)-(j) pin the unset topology and IntoChatOS:Bind (D-21).
public sealed class OpenPostureLoopbackFacts
{
    private const string OptIn = OpenPostureLoopback.AllowOpenOnNetworkKey;

    [Fact]
    public async Task A_LocalOpenOnLoopbackStartsAndLogsItsUrl()
    {
        var logs = new CapturingProvider();
        var exit = new RecordingExit();
        await using var app = Build(Local(("urls", "http://127.0.0.1:0")), logs, exit);
        await app.StartAsync(TestContext.Current.CancellationToken);
        Assert.Contains(logs.Messages, message => message.StartsWith("Open posture listening on http://127.0.0.1:", StringComparison.Ordinal));
        Assert.Equal(0, exit.Failures);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void B_LocalOpenOnAWildcardIsRefusedBeforeBindNamingTheOptIn()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OpenPostureLoopback.Validate(Configuration(Local(("urls", "http://0.0.0.0:0")))));
        Assert.Contains("http://0.0.0.0:0", exception.Message);
        Assert.Contains(OptIn, exception.Message);
        // The same refusal comes from the service defaults every server and client carries, before the host is built.
        var builder = WebApplication.CreateBuilder();
        foreach (var (key, value) in Local(("urls", "http://0.0.0.0:0"))) { builder.Configuration[key] = value; }
        builder.Configuration[LegacyConfigurationGuard.IgnoreLegacyDataDirectoryKey] = "true";
        Assert.Throws<InvalidOperationException>(() => builder.AddServiceDefaults());
    }

    [Fact]
    public void C_TheExplicitOptInAllowsTheWildcard()
        => OpenPostureLoopback.Validate(Configuration(Local(("urls", "http://0.0.0.0:0"), (OptIn, "true"))));

    [Fact]
    public void D_SecuredPostureMayListenOnTheNetwork()
        => OpenPostureLoopback.Validate(Configuration(Local(("urls", "http://0.0.0.0:0"), (OpenPostureLoopback.PostureKey, "Secured"))));

    [Theory]
    [InlineData("cloud")]
    [InlineData("container")]
    public void E_CloudAndContainersAreOutOfScope(string scope)
    {
        List<(string, string?)> values = [(OpenPostureLoopback.PostureKey, "Open"), ("urls", "http://+:8080")];
        if (scope == "container")
        {
            values.Add((Topology, IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyLocal));
            values.Add(("DOTNET_RUNNING_IN_CONTAINER", "true"));
        }
        else { values.Add((Topology, IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyCloud)); }
        OpenPostureLoopback.Validate(Configuration(values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    [InlineData("local-cloud")]
    public void H_UnsetLocalAndLocalCloudAllRefuseAWildcard(string? topology)
    {
        List<(string, string?)> values = [(OpenPostureLoopback.PostureKey, "Open"), ("urls", "http://0.0.0.0:0")];
        if (topology is not null) { values.Add((Topology, topology)); }
        var exception = Assert.Throws<InvalidOperationException>(() => OpenPostureLoopback.Validate(Configuration(values)));
        Assert.Contains(OptIn, exception.Message);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.10")]
    public void I_AnExplicitBindBeyondLoopbackIsRefusedForOpenPostureNamingTheBindAndThePosture(string bind)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OpenPostureLoopback.Validate(Configuration(Local(("urls", "http://127.0.0.1:0"), (Bind, bind)))));
        Assert.Contains(Bind, exception.Message);
        Assert.Contains("Open posture", exception.Message);
        Assert.Contains(OptIn, exception.Message);
        Assert.DoesNotContain(bind == "::" ? "[::]" : bind, exception.Message);
    }

    [Fact]
    public void J_AnExplicitBindWithTheOptInStartsAndALoopbackBindNeedsNoOptIn()
    {
        OpenPostureLoopback.Validate(Configuration(Local(("urls", "http://0.0.0.0:0"), (Bind, "0.0.0.0"), (OptIn, "true"))));
        OpenPostureLoopback.Validate(Configuration(Local(("urls", "http://127.0.0.1:0"), (Bind, "127.0.0.1,localhost"))));
    }

    [Theory]
    [InlineData("DOTNET_HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTPS_PORTS")]
    [InlineData("DOTNET_HTTPS_PORTS")]
    public void F_ABarePortBindsEveryInterfaceAndIsRefused(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => OpenPostureLoopback.Validate(Configuration(Local((key, "8080")))));
        Assert.Contains(key, exception.Message);
        Assert.Contains(OptIn, exception.Message);
    }

    [Theory]
    [InlineData("http://localhost:5080;https://[::1]:5081", true)]
    [InlineData("http://127.0.0.2:5080", true)]
    [InlineData("http://*:5080", false)]
    [InlineData("http://[::]:5080", false)]
    [InlineData("http://192.168.1.10:5080", false)]
    [InlineData("http://example.test:5080", false)]
    public void UrlClassification(string urls, bool loopback)
    {
        var configured = OpenPostureLoopback.NonLoopbackConfigured(Configuration(Local(("urls", urls))));
        Assert.Equal(loopback, configured.Count == 0);
    }

    [Fact]
    public void KestrelEndpointUrlsAreChecked()
    {
        var configured = OpenPostureLoopback.NonLoopbackConfigured(Configuration(Local(("Kestrel:Endpoints:Web:Url", "http://0.0.0.0:5080"))));
        Assert.Single(configured);
    }

    [Fact]
    public async Task G_AWildcardBoundInCodeIsStoppedByTheBackstopWithExitCodeOne()
    {
        var logs = new CapturingProvider();
        var exit = new RecordingExit();
        var started = false;
        await using var app = Build(Local(), logs, exit, web => web.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(0)));
        var stopping = new TaskCompletionSource();
        app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            started = true;
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally { if (started) { await app.StopAsync(TestContext.Current.CancellationToken); } }
        Assert.Equal(1, exit.Failures);
        Assert.Contains(logs.Errors, message => message.Contains(OptIn, StringComparison.Ordinal));
    }

    private const string Topology = IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyKey;
    private const string Bind = IntoChat.OS.Contracts.Configuration.IntoChatOSNames.BindKey;

    private static (string Key, string? Value)[] Local(params (string Key, string? Value)[] extra)
        => [(OpenPostureLoopback.PostureKey, "Open"), (Topology, IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyLocal), .. extra];

    private static IConfiguration Configuration(IEnumerable<(string Key, string? Value)> values)
    {
        var configuration = new ConfigurationManager();
        foreach (var (key, value) in values) { configuration[key] = value; }
        return configuration;
    }

    private static WebApplication Build((string Key, string? Value)[] values, CapturingProvider logs, RecordingExit exit, Action<IWebHostBuilder>? web = null)
    {
        var builder = WebApplication.CreateBuilder();
        foreach (var (key, value) in values) { builder.Configuration[key] = value; }
        // The guard's directory check is machine state, not what these facts are about.
        builder.Configuration[LegacyConfigurationGuard.IgnoreLegacyDataDirectoryKey] = "true";
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<OpenPostureLoopback.OpenPostureExit>(exit);
        web?.Invoke(builder.WebHost);
        builder.AddServiceDefaults();
        return builder.Build();
    }

    private sealed class RecordingExit : OpenPostureLoopback.OpenPostureExit
    {
        public int Failures;
        public override void Fail() => Interlocked.Increment(ref Failures);
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        public IReadOnlyList<string> Messages { get { lock (_entries) { return _entries.Select(entry => entry.Message).ToArray(); } } }
        public IReadOnlyList<string> Errors { get { lock (_entries) { return _entries.Where(entry => entry.Level == LogLevel.Error).Select(entry => entry.Message).ToArray(); } } }
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }

        private sealed class Capture(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { lock (owner._entries) { owner._entries.Add((logLevel, formatter(state, exception))); } }
        }
    }
}
