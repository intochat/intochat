using System.Collections.Concurrent;
using System.Net;
using System.Text;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class AcaJobCandidateHostFacts : IDisposable
{
    private const string SubscriptionId = "2a985480-6bb9-44ff-959d-edd5efe2cdb2";
    private readonly string _workdir;

    public AcaJobCandidateHostFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-acajob-host-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workdir))
        {
            try
            {
                Directory.Delete(_workdir, recursive: true);
            }
            catch
            {
                // Best-effort temp test directory cleanup.
            }
        }
    }

    [Fact]
    public async Task RunWaitHealthyAndRemove_ExecutesArmLifecycleWithScrubbedEnvAndManagedIdentity()
    {
        var handler = new FakeArmHandler();
        handler.PollStatuses.Enqueue("Processing");
        handler.PollStatuses.Enqueue("Running");

        using var http = new HttpClient(handler);
        var tokens = new FakeTokenProvider("managed-identity-arm-token");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.MasterKeyConfigurationKey] = "host-live-master-secret",
                [PresenceProof.TokenHashConfigurationKey] = "host-presence-hash",
                ["INTOCHAT_PRESENCE_TOKEN"] = "host-presence-token",
                ["SSH_AUTH_SOCK"] = "/tmp/host-ssh-agent.sock",
                ["GITHUB_TOKEN"] = "ghp_host_leaked_token",
                ["AWS_SECRET_ACCESS_KEY"] = "aws-leaked-secret",
                ["AZURE_CLIENT_SECRET"] = "azure-leaked-secret",
            })
            .Build();

        var modelKeyName = CandidateGitWorkspace.IntegrationVariable("openai", "ApiKey");
        using var host = new AcaJobCandidateHost(
            new NoopProcessRunner(),
            config,
            ValidOptions(),
            _ => Task.FromResult(_workdir),
            tokens,
            http,
            _ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>
            {
                [modelKeyName] = "explicit-candidate-openai-key",
                [IntoChatOSNames.MasterKeyEnvironmentVariable] = "attempted-custody-leak",
            })));

        var running = await host.Run("a1b2c3d", Ct);

        Assert.Equal("a1b2c3d", running.Sha);
        Assert.Equal("intochat-test-devslot-exec01", running.Container);
        Assert.Equal("https://management.azure.com/.default", tokens.LastScope);

        // Verify start request URI, bearer auth, and scrubbed payload.
        var startCall = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal));
        Assert.Equal("Bearer", startCall.AuthScheme);
        Assert.Equal("managed-identity-arm-token", startCall.AuthParameter);
        Assert.Contains("api-version=2025-01-01", startCall.Uri.Query, StringComparison.Ordinal);
        Assert.NotNull(startCall.Body);
        Assert.Contains("\"INTOCHAT_CANDIDATE_SHA\"", startCall.Body, StringComparison.Ordinal);
        Assert.Contains("\"a1b2c3d\"", startCall.Body, StringComparison.Ordinal);
        Assert.Contains("explicit-candidate-openai-key", startCall.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("host-live-master-secret", startCall.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("attempted-custody-leak", startCall.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("host-presence-token", startCall.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_host_leaked_token", startCall.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("aws-leaked-secret", startCall.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-leaked-secret", startCall.Body, StringComparison.Ordinal);

        Assert.Equal("/home/agent", host.LastRunEnvironment["HOME"]);
        Assert.Equal("/dev/null", host.LastRunEnvironment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("true", host.LastRunEnvironment[CandidateGitWorkspace.Env(IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey)]);
        Assert.False(host.LastRunEnvironment.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
        Assert.False(host.LastRunEnvironment.ContainsKey("GITHUB_TOKEN"));

        await host.WaitHealthy("a1b2c3d", TimeSpan.FromSeconds(5), Ct);
        var pollCalls = handler.Requests.Where(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith("/executions/intochat-test-devslot-exec01", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, pollCalls.Length);

        await host.Remove("a1b2c3d", Ct);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/executions/intochat-test-devslot-exec01/stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitHealthy_ThrowsCandidateHostException_WhenExecutionFails()
    {
        var handler = new FakeArmHandler();
        handler.PollStatuses.Enqueue("Failed");

        using var http = new HttpClient(handler);
        using var host = new AcaJobCandidateHost(
            new NoopProcessRunner(),
            new ConfigurationBuilder().Build(),
            ValidOptions(),
            _ => Task.FromResult(_workdir),
            new FakeTokenProvider(),
            http);

        await host.Run("deadb01", Ct);
        var ex = await Assert.ThrowsAsync<CandidateHostException>(() => host.WaitHealthy("deadb01", TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("healthy", ex.Stage);
        Assert.Contains("Failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orphans_StopsOnlyUnknownActiveExecutions_AndPreservesKnownAndCompleted()
    {
        var handler = new FakeArmHandler
        {
            ExecutionsListJson = """
                {
                  "value": [
                    {
                      "name": "intochat-test-devslot-known",
                      "properties": {
                        "status": "Running",
                        "template": { "containers": [ { "env": [ { "name": "INTOCHAT_CANDIDATE_SHA", "value": "aaa1111" } ] } ] }
                      }
                    },
                    {
                      "name": "intochat-test-devslot-orphan",
                      "properties": {
                        "status": "Running",
                        "template": { "containers": [ { "env": [ { "name": "INTOCHAT_CANDIDATE_SHA", "value": "bbb2222" } ] } ] }
                      }
                    },
                    {
                      "name": "intochat-test-devslot-done",
                      "properties": {
                        "status": "Succeeded",
                        "template": { "containers": [ { "env": [ { "name": "INTOCHAT_CANDIDATE_SHA", "value": "ccc3333" } ] } ] }
                      }
                    }
                  ]
                }
                """,
        };

        using var http = new HttpClient(handler);
        using var host = new AcaJobCandidateHost(
            new NoopProcessRunner(),
            new ConfigurationBuilder().Build(),
            ValidOptions(),
            _ => Task.FromResult(_workdir),
            new FakeTokenProvider(),
            http);

        var removed = await host.Orphans(["aaa1111"], Ct);

        Assert.Equal(["intochat-test-devslot-orphan"], removed);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/executions/intochat-test-devslot-orphan/stop", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("intochat-test-devslot-known/stop", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("intochat-test-devslot-done/stop", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidation_EnforcesPolicyRuleBCeilingsAndRejectsSsrfOrPathTraversal()
    {
        // Valid baseline passes (including sovereign ARM endpoints).
        ValidOptions().Validate();
        var gov = ValidOptions();
        gov.ArmEndpoint = "https://management.usgovcloudapi.net";
        gov.Validate();

        // Policy Rule B ceilings.
        var badTimeout = ValidOptions();
        badTimeout.ReplicaTimeoutSeconds = 901;
        Assert.Throws<ArgumentOutOfRangeException>(badTimeout.Validate);

        var badParallelism = ValidOptions();
        badParallelism.Parallelism = 3;
        Assert.Throws<ArgumentOutOfRangeException>(badParallelism.Validate);

        var badCpu = ValidOptions();
        badCpu.Cpu = 2.5;
        Assert.Throws<ArgumentOutOfRangeException>(badCpu.Validate);

        var badMemory = ValidOptions();
        badMemory.Memory = "8Gi";
        Assert.Throws<ArgumentOutOfRangeException>(badMemory.Validate);

        // SSRF / non-HTTPS / custom port / path injection.
        foreach (var badEndpoint in new[]
        {
            "http://management.azure.com",
            "https://evil.example.com",
            "https://management.azure.com:8443",
            "https://user:pass@management.azure.com",
            "https://management.azure.com/extra",
            "https://169.254.169.254",
        })
        {
            var opts = ValidOptions();
            opts.ArmEndpoint = badEndpoint;
            Assert.Throws<ArgumentException>(opts.Validate);
        }

        // Identifier / path traversal checks.
        var badSub = ValidOptions();
        badSub.SubscriptionId = "not-a-guid";
        Assert.Throws<ArgumentException>(badSub.Validate);

        var badRg = ValidOptions();
        badRg.ResourceGroup = "../other-rg";
        Assert.Throws<ArgumentException>(badRg.Validate);

        var badJob = ValidOptions();
        badJob.JobName = "job/../../evil";
        Assert.Throws<ArgumentException>(badJob.Validate);
    }

    [Fact]
    public async Task Run_RejectsPathTraversalInArmExecutionNameResponse()
    {
        var handler = new FakeArmHandler { StartExecutionName = "../evil-exec" };
        using var http = new HttpClient(handler);
        using var host = new AcaJobCandidateHost(
            new NoopProcessRunner(),
            new ConfigurationBuilder().Build(),
            ValidOptions(),
            _ => Task.FromResult(_workdir),
            new FakeTokenProvider(),
            http);

        var ex = await Assert.ThrowsAsync<CandidateHostException>(() => host.Run("a1b2c3d", Ct));
        Assert.Equal("run", ex.Stage);
    }

    [Fact]
    public void AspireModule_DefaultsToAgentDevSlots_AndFailsClosedWhenAcaJobConfigIsMissing()
    {
        var defaultOptions = new AspireOptions();
        Assert.Equal(CandidateHostMode.AgentDevSlots, defaultOptions.CandidateHostMode);
        defaultOptions.Validate();

        var unconfiguredAcaOptions = new AspireOptions
        {
            CandidateHostMode = CandidateHostMode.AcaJob,
        };
        Assert.Throws<ArgumentException>(unconfiguredAcaOptions.Validate);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("aspire")}:CandidateHostMode"] = "AcaJob",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Options.Create(unconfiguredAcaOptions));
        services.AddSingleton<DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<AspireModule>>(new FakeIntegrations());
        AspireModule.ConfigureServices(services, config);

        using var provider = services.BuildServiceProvider();
        Assert.Throws<ArgumentException>(provider.GetRequiredService<ICandidateHost>);
    }

    private static AcaJobCandidateOptions ValidOptions() => new()
    {
        SubscriptionId = SubscriptionId,
        ResourceGroup = "intochat-test",
        JobName = "intochat-test-devslot",
        ArmEndpoint = AcaJobCandidateOptions.DefaultArmEndpoint,
        ApiVersion = AcaJobCandidateOptions.DefaultApiVersion,
        ContainerName = "devslot",
        ImageOverride = "acrintochattest.azurecr.io/devslot:latest",
        ReplicaTimeoutSeconds = 900,
        Parallelism = 1,
        Cpu = 2.0,
        Memory = "4Gi",
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? AuthScheme, string? AuthParameter, string? Body);

    private sealed class FakeArmHandler : HttpMessageHandler
    {
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public Queue<string> PollStatuses { get; } = new();
        public string StartExecutionName { get; set; } = "intochat-test-devslot-exec01";
        public string ExecutionsListJson { get; set; } = """{ "value": [] }""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            Requests.Enqueue(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/start", StringComparison.Ordinal))
            {
                return JsonResponse($$"""{ "name": "{{StartExecutionName}}" }""");
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/executions", StringComparison.Ordinal))
            {
                return JsonResponse(ExecutionsListJson);
            }

            if (request.Method == HttpMethod.Get && path.Contains("/executions/", StringComparison.Ordinal))
            {
                var status = PollStatuses.Count > 0 ? PollStatuses.Dequeue() : "Running";
                return JsonResponse($$"""{ "name": "{{StartExecutionName}}", "properties": { "status": "{{status}}" } }""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/stop", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class FakeTokenProvider(string token = "fake-arm-token") : IAcaJobTokenProvider
    {
        public string? LastScope { get; private set; }

        public Task<string> GetTokenAsync(string armScope, CancellationToken ct)
        {
            LastScope = armScope;
            return Task.FromResult(token);
        }
    }

    private sealed class NoopProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, "", ""));

        public int Detach(string file, string[] args, string workingDirectory) => 1;
    }

    private sealed class FakeIntegrations : DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<AspireModule>
    {
        public DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle Get(string integrationId) => throw new NotSupportedException();
    }
}
