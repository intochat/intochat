using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.Aspire.Candidates;
using IntoChat.Aspire;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class AspireFacts
{
    [Fact]
    public void TheDefaultOptionsValidate()
        => new AspireOptions().Validate();

    [Fact]
    public void ARollStepBudgetOfZeroOrLessIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { SiloStepBudget = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { SiloStepBudget = TimeSpan.FromSeconds(-1) }.Validate());
    }

    [Fact]
    public void TheRestartConfirmationExpiryDefaultsToTheFrameworksDefaultExpiry()
        => Assert.Equal(DigitalBrain.Confirmation.ConfirmationRules.DefaultExpiry, new AspireOptions().RestartConfirmationExpiry);

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("7.00:00:01")]
    public void ARestartConfirmationExpiryOfZeroNegativeOrMoreThanSevenDaysIsRefused(string value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { RestartConfirmationExpiry = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }.Validate());

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("7.00:00:00")]
    public void ARestartConfirmationExpiryFromThirtySecondsToSevenDaysValidates(string value)
        => new AspireOptions { RestartConfirmationExpiry = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }.Validate();

    [Fact]
    public void AClusterSamplingIntervalOfZeroOrLessIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { ClusterSampling = TimeSpan.Zero }.Validate());

    [Fact]
    public void ANewCandidateQueueHasNothingLiveAndNothingQueued()
    {
        var state = new CandidateQueueState();

        Assert.Null(state.Live);
        Assert.Empty(state.Queue);
    }

    [Fact]
    public void AQueueStateChangesByCopyingAndNeverMutatesTheOriginal()
    {
        var idle = new CandidateQueueState();

        var queued = idle with { Queue = ["c1", "c2"] };
        var promoted = queued with { Live = "c1", Queue = [.. queued.Queue.Skip(1)] };

        Assert.Empty(idle.Queue);
        Assert.Equal(["c1", "c2"], queued.Queue);
        Assert.Equal("c1", promoted.Live);
        Assert.Equal(["c2"], promoted.Queue);
    }

    [Fact]
    public async Task AspireNeuronRecordsAndQueriesLogsAndTracesOnTestOS()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local();
        os.Register<AspireNeuron>();
        var aspire = os.Get<IAspire>(new NeuronId(new BrainId("local"), "aspire", "aspire"));

        var resources = await aspire.GetResourcesAsync();
        Assert.DoesNotContain(resources, r => r.Name == "os" || r.Name == "intochat-os-mcp");
        Assert.Empty(resources);

        await aspire.RecordLogAsync(new AspireLogEntry(DateTimeOffset.UtcNow, "os", "Error", "NullReferenceException in CalculatorApp", "System.NullReferenceException"));
        await aspire.RecordTraceAsync(new AspireTraceSpan("trace-1", "span-1", "os", "calculator.add", "Error", 1250, "DivideByZeroException"));

        var errorLogs = await aspire.GetStructuredLogsAsync("os", "Error");
        Assert.Single(errorLogs);
        var consoleLines = await aspire.GetConsoleLogsAsync("os", 10);
        Assert.Single(consoleLines);
        var errorTraces = await aspire.GetTracesAsync("os", errorsOnly: true);
        Assert.Contains(errorTraces, t => t.TraceId == "trace-1" && t.Name == "calculator.add" && t.Error == "DivideByZeroException");
    }

    // Stored telemetry and older deployments name the kernel OperatingSystem; either name finds both.
    [Fact]
    public async Task TelemetryStoredUnderTheOldKernelNameIsFoundByTheNewOneAndBack()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local();
        os.Register<AspireNeuron>();
        var aspire = os.Get<IAspire>(new NeuronId(new BrainId("local"), "aspire", "aspire"));

        await aspire.RecordLogAsync(new AspireLogEntry(DateTimeOffset.UtcNow, "OperatingSystem", "Error", "old log", null));
        await aspire.RecordLogAsync(new AspireLogEntry(DateTimeOffset.UtcNow, "os", "Error", "new log", null));
        await aspire.RecordLogAsync(new AspireLogEntry(DateTimeOffset.UtcNow, "ai", "Error", "other log", null));
        await aspire.RecordTraceAsync(new AspireTraceSpan("t-old", "s-old", "OperatingSystem", "old.span", "Error", 1, "x"));
        await aspire.RecordTraceAsync(new AspireTraceSpan("t-new", "s-new", "os", "new.span", "Error", 1, "x"));

        foreach (var name in new[] { "os", "OperatingSystem" })
        {
            Assert.Equal(["new log", "old log"], (await aspire.GetStructuredLogsAsync(name)).Select(log => log.Message).Order());
            Assert.Equal(2, (await aspire.GetConsoleLogsAsync(name, 10)).Length);
            Assert.Equal(["new.span", "old.span"], (await aspire.GetTracesAsync(name)).Where(span => span.Name.EndsWith(".span", StringComparison.Ordinal)).Select(span => span.Name).Order());
        }
        Assert.Equal(["other log"], (await aspire.GetStructuredLogsAsync("ai")).Select(log => log.Message));
    }

    [Fact]
    public void AnOldDeploymentsKernelResourceIsReportedAsOs()
    {
        var baseline = new[] { new AspireResourceView("OperatingSystem", "Running", "http://127.0.0.1:6200"), new AspireResourceView("ai", "Running", "") };

        var resources = AspireLiveTelemetry.DiscoverResources(baseline, new global::Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Equal(["os", "ai"], resources.Select(resource => resource.Name));
        Assert.Equal("http://127.0.0.1:6200", resources[0].Endpoint);
    }

    [Fact]
    public void TheKernelRuleNamesTheSameStringsAsTheAppHost()
    {
        // Mirrors IntoChat.OS.Aspire.Hosting.ResourceNames (OsInstances): two assemblies, one rule.
        Assert.True(KernelNames.IsInstance("os", "os"));
        Assert.True(KernelNames.IsInstance("os-a", "os"));
        Assert.True(KernelNames.IsInstance("os-b", "os"));
        Assert.False(KernelNames.IsInstance("os-gateway", "os"));
        Assert.False(KernelNames.IsInstance("OperatingSystem-a", "os"));
        Assert.False(KernelNames.IsInstance("os-a", "OperatingSystem"));
        Assert.True(KernelNames.IsInstance("OperatingSystem-a", "OperatingSystem"));
        Assert.False(KernelNames.IsInstance("OperatingSystem-front", "OperatingSystem"));
        Assert.Equal("os", KernelNames.RoleOf("os"));
        Assert.Equal("os", KernelNames.RoleOf("os-a"));
        Assert.Equal("OperatingSystem", KernelNames.RoleOf("OperatingSystem-a"));
        Assert.Equal(["os-a", "os-b"], IntoChat.Aspire.Deployment.AspireCliClusterHost.Silos(["os-a", "os-gateway", "os-b", "ai"]));
    }

    [Fact]
    public void AspireDiscoverResourcesReportsOnlyConfiguredEndpoints()
    {
        var cfg = global::Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            new global::Microsoft.Extensions.Configuration.ConfigurationBuilder(),
            new Dictionary<string, string?>
            {
                ["INTOCHAT_OS_URL"] = "http://127.0.0.1:6100"
            })
            .Build();

        var resources = AspireLiveTelemetry.DiscoverResources([], cfg);
        var osResource = Assert.Single(resources);
        Assert.Equal("os", osResource.Name);
        Assert.Equal("http://127.0.0.1:6100", osResource.Endpoint);
    }

    [Fact]
    public void DockerCandidateHostExposesContainerResourceLimits()
    {
        Assert.Equal("2g", DockerCandidateHost.CandidateMemoryLimit);
        Assert.Equal("2", DockerCandidateHost.CandidateCpuLimit);
    }

    [Fact]
    public async Task DockerCandidateHostEnforcesMemoryAndCpuCapsAndCanaryRollback()
    {
        var runner = new RecordingCandidateProcessRunner();
        using var http = new HttpClient(new AlwaysHealthyHttpMessageHandler());
        var config = new global::Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var host = new DockerCandidateHost(
            runner,
            config,
            _ => Task.FromResult(Path.Combine(Path.GetTempPath(), "intochat-candidate-workdir")),
            _ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>())),
            _ => false,
            http,
            (_, _, _) => Task.CompletedTask);

        var running = await host.Run("a1b2c3d", TestContext.Current.CancellationToken);
        Assert.Equal("a1b2c3d", running.Sha);
        Assert.Equal("candidate-a1b2c3d", running.Container);

        var osRun = Assert.Single(runner.Calls, c => c.File == "docker" && c.Args.Contains("candidate-a1b2c3d") && c.Args.Contains("--hostname"));
        Assert.Contains("--memory", osRun.Args);
        Assert.Contains("2g", osRun.Args);
        Assert.Contains("--cpus", osRun.Args);
        Assert.Contains("2", osRun.Args);

        Assert.NotNull(running.Endpoints.Mcp);
        Assert.Equal(running.Endpoints.Mcp, running.Endpoints.Ui);
        Assert.DoesNotContain(runner.Calls, c => c.File == "docker" && c.Args.Contains("candidate-a1b2c3d-mcp"));

        await host.WaitHealthy("a1b2c3d", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await host.Uncommit("a1b2c3d", "0123456", TestContext.Current.CancellationToken);
        Assert.Contains(runner.Calls, c => c.File == "git" && c.Args.Contains("update-ref") && c.Args.Contains(DockerCandidateHost.LiveRef) && c.Args.Contains("0123456"));
    }

    private sealed record RecordedCall(string File, string[] Args);

    private sealed class RecordingCandidateProcessRunner : IProcessRunner
    {
        public List<RecordedCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        {
            Calls.Add(new RecordedCall(file, args));
            if (file == "docker" && args is ["inspect", "-f", "{{.State.Running}}", _])
            {
                return Task.FromResult(new ProcessResult(0, "true\n", ""));
            }
            if (file == "git" && args.Contains("rev-parse"))
            {
                return Task.FromResult(new ProcessResult(0, "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4\n", ""));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public int Detach(string file, string[] args, string workingDirectory) => 1;
    }

    private sealed class AlwaysHealthyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
