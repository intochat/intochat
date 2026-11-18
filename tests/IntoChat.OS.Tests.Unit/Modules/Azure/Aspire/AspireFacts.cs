using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests.Unit;

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
        Assert.DoesNotContain(resources, r => r.Name == "OperatingSystem" || r.Name == "intochat-os-mcp");
        Assert.Empty(resources);

        await aspire.RecordLogAsync(new AspireLogEntry(DateTimeOffset.UtcNow, "OperatingSystem", "Error", "NullReferenceException in CalculatorApp", "System.NullReferenceException"));
        await aspire.RecordTraceAsync(new AspireTraceSpan("trace-1", "span-1", "OperatingSystem", "calculator.add", "Error", 1250, "DivideByZeroException"));

        var errorLogs = await aspire.GetStructuredLogsAsync("OperatingSystem", "Error");
        Assert.Single(errorLogs);
        var consoleLines = await aspire.GetConsoleLogsAsync("OperatingSystem", 10);
        Assert.Single(consoleLines);
        var errorTraces = await aspire.GetTracesAsync("OperatingSystem", errorsOnly: true);
        Assert.Contains(errorTraces, t => t.TraceId == "trace-1" && t.Name == "calculator.add" && t.Error == "DivideByZeroException");
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
        Assert.Equal("OperatingSystem", osResource.Name);
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
