using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Modules.Configuration;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class AcaDynamicSessionsCandidateHostFacts : IDisposable
{
    private const string PoolEndpoint =
        "https://eastus.dynamicsessions.io/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-intochat/sessionPools/pool-plane2";

    private readonly string _workdir;

    public AcaDynamicSessionsCandidateHostFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-aca-host-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Run_WaitHealthy_Remove_AndOrphans_EnforceSessionIsolation_BearerAuth_AndSecretContainment()
    {
        const string bearerToken = "eyJhbGciOiJSUzI1NiJ9.plane2-aca-bearer-token.sig";
        var requests = new ConcurrentQueue<CapturedRequest>();
        var healthChecks = 0;

        using var http = new HttpClient(new StubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
            requests.Enqueue(new CapturedRequest(
                req.Method,
                req.RequestUri!,
                req.Headers.Authorization?.Scheme,
                req.Headers.Authorization?.Parameter,
                body));

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/executions", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, new
                {
                    status = "Succeeded",
                    result = new
                    {
                        handled = true,
                        mcpEndpoint = "https://candidate-session.dynamicsessions.io/mcp",
                        uiEndpoint = "https://candidate-session.dynamicsessions.io/ui",
                    },
                });
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref healthChecks);
                return new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, new
                {
                    value = new[]
                    {
                        new { identifier = "p2-run-a1b2c3d-111111111111", status = "Running" },
                        new { identifier = "p2-run-deadbee-222222222222", status = "Running" },
                        new { identifier = "p1-brain-app-333333333333", status = "Running" }, // Plane 1 session must NOT be reaped as Plane 2 orphan!
                    },
                });
            }

            if (req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var aspireSection = ModuleConfiguration.ModuleOptionsSection("aspire");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.MasterKeyConfigurationKey] = "host-live-master-secret",
                [PresenceProof.TokenHashConfigurationKey] = "host-presence-hash",
                ["INTOCHAT_PRESENCE_TOKEN"] = "host-presence-token",
                ["SSH_AUTH_SOCK"] = "/tmp/host-ssh.sock",
                ["GITHUB_TOKEN"] = "ghp_host_leaked_token_123456",
                ["AWS_SECRET_ACCESS_KEY"] = "aws-leaked-secret",
                ["IDENTITY_HEADER"] = "msi-secret-header",
                [$"{aspireSection}:CommandTimeout"] = "00:02:00",
            })
            .Build();

        var modelKeyName = DockerCandidateHost.IntegrationVariable("openai", "ApiKey");
        var acaOpts = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint };
        var client = new AcaDynamicSessionsClient(http, acaOpts, new DelegateTokenProvider(_ => Task.FromResult(bearerToken)));
        await using var host = new AcaDynamicSessionsCandidateHost(
            new FakeProcessRunner(),
            config,
            client,
            _ => Task.FromResult(_workdir),
            _ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>
            {
                [modelKeyName] = "explicit-candidate-openai-key",
                [IntoChatOSNames.MasterKeyEnvironmentVariable] = "attempted-host-master-injection",
            })),
            healthPollInterval: TimeSpan.FromMilliseconds(10));

        // 1. Run two candidates -> distinct isolated session IDs.
        var run1 = await host.Run("a1b2c3d", Ct);
        var run2 = await host.Run("b2c3d4e", Ct);
        Assert.NotEqual(run1.Container, run2.Container);
        Assert.StartsWith("p2-run-a1b2c3d-", run1.Container, StringComparison.Ordinal);
        Assert.StartsWith("p2-run-b2c3d4e-", run2.Container, StringComparison.Ordinal);
        Assert.Equal("https://candidate-session.dynamicsessions.io/mcp", run1.Endpoints.Mcp);
        Assert.Equal("https://candidate-session.dynamicsessions.io/ui", run1.Endpoints.Ui);

        // 2. Verify zero host secret leakage in LastRunEnvironment AND outbound JSON body.
        var env = host.LastRunEnvironment;
        Assert.Equal("explicit-candidate-openai-key", env[modelKeyName]);
        Assert.True(env.TryGetValue(IntoChatOSNames.MasterKeyEnvironmentVariable, out var candidateMasterKey));
        Assert.False(string.IsNullOrWhiteSpace(candidateMasterKey));
        Assert.NotEqual("host-live-master-secret", candidateMasterKey);
        Assert.NotEqual("attempted-host-master-injection", candidateMasterKey);
        Assert.False(env.ContainsKey("INTOCHAT_PRESENCE_TOKEN"));
        Assert.False(env.ContainsKey("SSH_AUTH_SOCK"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("AWS_SECRET_ACCESS_KEY"));
        Assert.False(env.ContainsKey("IDENTITY_HEADER"));

        var postBodies = requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Body).ToArray();
        Assert.Equal(2, postBodies.Length);
        foreach (var body in postBodies)
        {
            Assert.DoesNotContain("host-live-master-secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain("host-presence-hash", body, StringComparison.Ordinal);
            Assert.DoesNotContain("host-presence-token", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ghp_host_leaked_token_123456", body, StringComparison.Ordinal);
            Assert.DoesNotContain("aws-leaked-secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain("msi-secret-header", body, StringComparison.Ordinal);
            Assert.DoesNotContain(bearerToken, body, StringComparison.Ordinal);
        }

        // 3. WaitHealthy retries 503 -> succeeds on 200.
        await host.WaitHealthy("a1b2c3d", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(2, healthChecks);

        // 4. Orphans removes ONLY unknown Plane 2 sessions (deadbee), never known (a1b2c3d) or Plane 1 (p1-*).
        var removedOrphans = await host.Orphans(["a1b2c3d"], Ct);
        Assert.Equal(["p2-run-deadbee-222222222222"], removedOrphans);

        // 5. Remove terminates active session cleanly.
        await host.Remove("a1b2c3d", Ct);
        await host.Remove("b2c3d4e", Ct);

        Assert.All(requests, r =>
        {
            Assert.Equal("Bearer", r.AuthScheme);
            Assert.Equal(bearerToken, r.AuthParameter);
        });
    }

    [Fact]
    public async Task BuildGate_AndPublish_UseIsolatedSessions_AndAlwaysTearDownEvenOnFailure()
    {
        var calls = new ConcurrentQueue<(HttpMethod Method, Uri Uri)>();
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            calls.Enqueue((req.Method, req.RequestUri!));
            if (req.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (req.RequestUri!.Query.Contains("p2-gate-", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    status = "Failed",
                    result = new { exitCode = 1, error = "CS1002: ; expected" },
                }));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { status = "Succeeded" }));
        }));

        var runner = new FakeProcessRunner(gitDiffOutput: "sample/AspireModule.cs\n");
        var client = new AcaDynamicSessionsClient(
            http,
            new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint },
            new DelegateTokenProvider(_ => Task.FromResult("tok")));
        await using var host = new AcaDynamicSessionsCandidateHost(
            runner,
            new ConfigurationBuilder().Build(),
            client,
            _ => Task.FromResult(_workdir));

        // BuildGate fails with CandidateHostException("gate", ...) AND still issues DELETE teardown!
        var gateEx = await Assert.ThrowsAsync<CandidateHostException>(() => host.BuildGate("a1b2c3d", Ct));
        Assert.Equal("gate", gateEx.Stage);
        Assert.Contains("CS1002", gateEx.Message, StringComparison.Ordinal);

        // Publish succeeds in its own isolated p2-pub- session and tears it down.
        var buildDir = await host.Publish("a1b2c3d", Ct);
        Assert.Equal(Path.Combine(_workdir, "builds", "a1b2c3d"), buildDir);

        var list = calls.ToArray();
        Assert.Contains(list, c => c.Method == HttpMethod.Post && c.Uri.Query.Contains("p2-gate-a1b2c3d-", StringComparison.Ordinal));
        Assert.Contains(list, c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.Contains("/sessions/p2-gate-a1b2c3d-", StringComparison.Ordinal));
        Assert.Contains(list, c => c.Method == HttpMethod.Post && c.Uri.Query.Contains("p2-pub-a1b2c3d-", StringComparison.Ordinal));
        Assert.Contains(list, c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.Contains("/sessions/p2-pub-a1b2c3d-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnconfiguredEndpoint_FailsClosedOnAllOperations_WithZeroNetworkCalls()
    {
        var httpCalls = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref httpCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var unconfiguredOpts = new AcaDynamicSessionsOptions { PoolManagementEndpoint = null };
        var client = new AcaDynamicSessionsClient(http, unconfiguredOpts, new DelegateTokenProvider(_ => Task.FromResult("unused")));
        await using var host = new AcaDynamicSessionsCandidateHost(
            new FakeProcessRunner(),
            new ConfigurationBuilder().Build(),
            client,
            _ => Task.FromResult(_workdir));

        Assert.False(host.IsConfigured);

        Assert.Equal("gate", (await Assert.ThrowsAsync<CandidateHostException>(() => host.BuildGate("a1b2c3d", Ct))).Stage);
        Assert.Equal("publish", (await Assert.ThrowsAsync<CandidateHostException>(() => host.Publish("a1b2c3d", Ct))).Stage);
        Assert.Equal("run", (await Assert.ThrowsAsync<CandidateHostException>(() => host.Run("a1b2c3d", Ct))).Stage);
        Assert.Equal("healthy", (await Assert.ThrowsAsync<CandidateHostException>(() => host.WaitHealthy("a1b2c3d", TimeSpan.FromSeconds(1), Ct))).Stage);
        Assert.Equal("orphans", (await Assert.ThrowsAsync<CandidateHostException>(() => host.Orphans([], Ct))).Stage);

        Assert.Equal(0, httpCalls);
    }

    [Fact]
    public async Task WaitHealthy_EnforcesStrictTimeout_AndCallerCancellation()
    {
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { status = "Succeeded" }));
            }

            if (req.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));

        var client = new AcaDynamicSessionsClient(
            http,
            new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint },
            new DelegateTokenProvider(_ => Task.FromResult("tok")));
        await using var host = new AcaDynamicSessionsCandidateHost(
            new FakeProcessRunner(),
            new ConfigurationBuilder().Build(),
            client,
            _ => Task.FromResult(_workdir),
            healthPollInterval: TimeSpan.FromMilliseconds(15));

        await host.Run("a1b2c3d", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => host.WaitHealthy("a1b2c3d", TimeSpan.FromMilliseconds(90), Ct));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.WaitHealthy("a1b2c3d", TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public void AspireModule_DiRegistration_IsDormantByDefault_AndResolvesAcaHostWhenConfigured()
    {
        // Default configuration: AspireOptions validates cleanly and registers DockerCandidateHost (ACA dormant).
        var defaultOptions = new AspireOptions();
        defaultOptions.Validate();
        Assert.Null(defaultOptions.AcaPoolManagementEndpoint);

        var defaultServices = new ServiceCollection();
        var defaultConfig = new ConfigurationBuilder().Build();
        defaultServices.AddSingleton<IConfiguration>(defaultConfig);
        defaultServices.AddSingleton<IModuleIntegrations<AspireModule>>(new StubIntegrations());
        AspireModule.ConfigureServices(defaultServices, defaultConfig);
        using var defaultProvider = defaultServices.BuildServiceProvider();
        Assert.IsType<DockerCandidateHost>(defaultProvider.GetRequiredService<ICandidateHost>());

        // Explicit CandidateHostMode = AcaDynamicSessions: resolves AcaDynamicSessionsCandidateHost cleanly even before endpoint is set.
        var section = ModuleConfiguration.ModuleOptionsSection("aspire");
        var acaConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{section}:CandidateHostMode"] = nameof(CandidateHostMode.AcaDynamicSessions),
            })
            .Build();
        var acaServices = new ServiceCollection();
        acaServices.AddSingleton<IConfiguration>(acaConfig);
        acaServices.AddSingleton<IModuleIntegrations<AspireModule>>(new StubIntegrations());
        acaServices.Configure<AspireOptions>(acaConfig.GetSection(section));
        AspireModule.ConfigureServices(acaServices, acaConfig);
        using var acaProvider = acaServices.BuildServiceProvider();
        var resolved = acaProvider.GetRequiredService<ICandidateHost>();
        var acaHost = Assert.IsType<AcaDynamicSessionsCandidateHost>(resolved);
        Assert.False(acaHost.IsConfigured);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthScheme,
        string? AuthParameter,
        string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class DelegateTokenProvider(Func<string, Task<string>> acquire) : IAcaDynamicSessionsTokenProvider
    {
        public Task<string> AcquireTokenAsync(string scope, CancellationToken cancellationToken = default)
            => acquire(scope);
    }

    private sealed class FakeProcessRunner(string gitDiffOutput = "") : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string file,
            string[] args,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, gitDiffOutput, string.Empty));

        public int Detach(string file, string[] args, string workingDirectory) => 12345;
    }

    private sealed class StubIntegrations : IModuleIntegrations<AspireModule>
    {
        public IIntegrationHandle Get(string integrationId) => throw new NotImplementedException();
    }
}
