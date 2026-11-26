using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.Aspire.Candidates;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class AcaDynamicSessionsFallbackIntegrationFacts
{
    private const string PoolEndpoint =
        "https://eastus.dynamicsessions.io/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-intochat/sessionPools/pool-kata";

    private const string PureComputeScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                return Task.FromResult(new OrchestrationResult(
                    Status: "succeeded",
                    Summary: "Computed in ACA Dynamic Sessions",
                    Artifacts: [new OrchestrationArtifact("report.json", "json", "{\"ok\":true}")],
                    Metrics: new Dictionary<string, double> { ["score"] = 99 }));
            }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CodeOrchestrationSynapse_RoutesToAcaDynamicSessions_WhenSovereignClientOfflineAndLocalSandboxDisabled()
    {
        const string bearerToken = "eyJ.integration-aca-bearer-secret.sig";
        const string enrollKey = "integration-host-enroll-secret";
        const string signingKey = "integration-host-token-signing-secret";

        var sessionIds = new ConcurrentBag<string>();
        var deletedSessions = new ConcurrentBag<string>();
        var seenPayloads = new ConcurrentQueue<string>();
        var seenAuthHeaders = new ConcurrentQueue<string>();

        using var acaHttp = new HttpClient(new StubHandler(async (req, ct) =>
        {
            seenAuthHeaders.Enqueue($"{req.Headers.Authorization?.Scheme} {req.Headers.Authorization?.Parameter}");

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/executions", StringComparison.Ordinal))
            {
                var sid = req.Headers.GetValues("X-IntoChat-Session-Id").Single();
                sessionIds.Add(sid);
                var body = await req.Content!.ReadAsStringAsync(ct);
                seenPayloads.Enqueue(body);

                var resultPayload = CodeOrchestrationScriptWrapper.SerializeResultJson(new CodeOrchestrationResult(
                    Status: "succeeded",
                    Summary: $"Completed in Kata MicroVM session {sid}",
                    Artifacts: [new CodeOrchestrationArtifact("report.json", "json", "{\"ok\":true}")],
                    Metrics: [new CodeOrchestrationMetric("score", 99)]));

                return JsonResponse(HttpStatusCode.OK, new
                {
                    status = "Succeeded",
                    result = new
                    {
                        handled = true,
                        resultJson = resultPayload,
                    },
                });
            }

            if (req.Method == HttpMethod.Delete)
            {
                var sid = req.Headers.GetValues("X-IntoChat-Session-Id").Single();
                deletedSessions.Add(sid);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var acaOptions = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint };
        var sandboxOptions = Options.Create(new SandboxOptions
        {
            EnableLocalSandbox = false,
            EnrollKey = enrollKey,
            TokenSigningMaterial = signingKey,
            KernelCallbackUrl = "http://127.0.0.1:5080",
            AcaDynamicSessions = acaOptions,
        });

        var hostServices = new ServiceCollection();
        hostServices.AddHttpClient();
        await using var hostSp = hostServices.BuildServiceProvider();

        var tokens = new AppTokenService(sandboxOptions);
        var acaClient = new AcaDynamicSessionsClient(
            acaHttp,
            acaOptions,
            new DelegateTokenProvider(_ => Task.FromResult(bearerToken)));
        var acaSandbox = new AcaDynamicSessionsSandbox(
            acaClient,
            sandboxOptions,
            tokens,
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero));
        var router = new ConnectedComputeRouter(clock: clock);
        var brain = new BrainId("brain-aca-fallback");

        await using var bridge = new HttpSandboxRuntimeBridge(
            hostSp.GetRequiredService<IHttpClientFactory>(),
            sandboxOptions,
            tokens,
            hostSp,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router,
            acaSandbox);

        var osServices = new ServiceCollection();
        osServices.AddSingleton(new AppPackageCompiler());
        osServices.AddSingleton<ICodeOrchestrationAstSanitizer, CodeOrchestrationAstSanitizer>();
        osServices.AddSingleton<ISandboxRuntimeBridge>(bridge);
        osServices.AddSingleton<ICodeSandbox>(bridge);
        osServices.AddSingleton<IConnectedComputeRouter>(router);

        await using var os = TestOS.Local(o => o.Services = osServices.BuildServiceProvider())
            .Register<CodeOrchestrationSynapse>();

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        // Run two flows back-to-back to verify Kata MicroVM session isolation end-to-end.
        for (var i = 1; i <= 2; i++)
        {
            var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, $"aca-run-{i}");
            var starter = os.Get<IFlowStarter>(flowId);
            var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
            {
                Goal = $"Run headless ACA fallback #{i}",
                InputJson = $"{{\"run\":{i}}}",
                AllowedContracts = ["notifications"],
                PreauthoredScript = PureComputeScript,
            }, CodeOrchestrationSynapse.Json);

            await starter.StartFromJson($"ACA Fallback Run #{i}", "owner", null, innerJson);
            await os.Idle();

            var head = await starter.Head();
            Assert.Equal(FlowStatus.Completed, head.Status);
            var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
            Assert.Equal("aca-dynamic-sessions", inner.ExecutionTarget);
            Assert.Null(inner.ExecutedByMachineId);
            Assert.Contains("ACA Dynamic Sessions", inner.RouteReason, StringComparison.Ordinal);
            Assert.NotNull(inner.Result);
            Assert.Equal("succeeded", inner.Result!.Status);
            Assert.Contains(inner.Result.Metrics, m => m.Name == "score" && m.Value == 99);
        }

        // Verify strict session isolation & mandatory session teardown.
        var allocated = sessionIds.ToArray();
        Assert.Equal(2, allocated.Length);
        Assert.NotEqual(allocated[0], allocated[1]);
        Assert.Equal(2, deletedSessions.Count);
        foreach (var sid in allocated)
        {
            Assert.Contains(sid, deletedSessions);
        }

        // Verify bearer token headers & zero host secret leakage across every request.
        Assert.All(seenAuthHeaders, h => Assert.Equal($"Bearer {bearerToken}", h));
        Assert.All(seenPayloads, body =>
        {
            Assert.DoesNotContain(bearerToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(enrollKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain(signingKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CodeOrchestrationSynapse_FailsClosed_WhenNoTierAvailableAndAcaUnconfigured()
    {
        var unconfiguredOptions = Options.Create(new SandboxOptions
        {
            EnableLocalSandbox = false,
            TokenSigningMaterial = "integration-unconfigured-signing-key",
            AcaDynamicSessions = new AcaDynamicSessionsOptions { PoolManagementEndpoint = null },
        });

        var hostServices = new ServiceCollection();
        hostServices.AddHttpClient();
        await using var hostSp = hostServices.BuildServiceProvider();

        var tokens = new AppTokenService(unconfiguredOptions);
        using var acaHttp = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("Unconfigured ACA client must never send HTTP requests.")));
        var dormantAca = new AcaDynamicSessionsSandbox(
            new AcaDynamicSessionsClient(acaHttp, unconfiguredOptions.Value.AcaDynamicSessions, new DelegateTokenProvider(_ => Task.FromResult("unused"))),
            unconfiguredOptions,
            tokens,
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        var router = new ConnectedComputeRouter(clock: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var bridge = new HttpSandboxRuntimeBridge(
            hostSp.GetRequiredService<IHttpClientFactory>(),
            unconfiguredOptions,
            tokens,
            hostSp,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router,
            dormantAca);

        var osServices = new ServiceCollection();
        osServices.AddSingleton(new AppPackageCompiler());
        osServices.AddSingleton<ICodeOrchestrationAstSanitizer, CodeOrchestrationAstSanitizer>();
        osServices.AddSingleton<ISandboxRuntimeBridge>(bridge);
        osServices.AddSingleton<ICodeSandbox>(bridge);
        osServices.AddSingleton<IConnectedComputeRouter>(router);

        await using var os = TestOS.Local(o => o.Services = osServices.BuildServiceProvider())
            .Register<CodeOrchestrationSynapse>();

        var brain = new BrainId("brain-fail-closed");
        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var starter = os.Get<IFlowStarter>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "fail-closed-1"));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Must fail closed when no sandbox tier is available",
            PreauthoredScript = PureComputeScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Fail Closed Check", "owner", null, innerJson);
        await os.Idle();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Failed, head.Status);
    }

    [Fact]
    public async Task Plane2_AcaDynamicSessionsCandidateHost_EndToEndGitWorktreeAndSessionLifecycle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "intochat-aca-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var repoDir = Path.Combine(tempRoot, "repo");
        var workdir = Path.Combine(repoDir, ".work");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(workdir);

        try
        {
            var runner = new SystemProcessRunner();
            Assert.Equal(0, (await runner.RunAsync("git", ["init"], repoDir, null, Ct)).ExitCode);
            Assert.Equal(0, (await runner.RunAsync("git", ["config", "user.email", "test@intochat.local"], repoDir, null, Ct)).ExitCode);
            Assert.Equal(0, (await runner.RunAsync("git", ["config", "user.name", "IntoChat Test"], repoDir, null, Ct)).ExitCode);
            await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "initial\n", Ct);
            Assert.Equal(0, (await runner.RunAsync("git", ["add", "."], repoDir, null, Ct)).ExitCode);
            Assert.Equal(0, (await runner.RunAsync("git", ["commit", "-m", "init"], repoDir, null, Ct)).ExitCode);
            var baseSha = (await runner.RunAsync("git", ["rev-parse", "--short", "HEAD"], repoDir, null, Ct)).Output.Trim();

            var seenActions = new ConcurrentQueue<string>();
            using var acaHttp = new HttpClient(new StubHandler(async (req, ct) =>
            {
                if (req.Method == HttpMethod.Post)
                {
                    var body = await req.Content!.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    var action = doc.RootElement.GetProperty("properties").GetProperty("action").GetString() ?? "";
                    seenActions.Enqueue(action);
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        status = "Succeeded",
                        result = new
                        {
                            handled = true,
                            mcpEndpoint = "https://cand.dynamicsessions.io/mcp",
                            uiEndpoint = "https://cand.dynamicsessions.io/ui",
                        },
                    });
                }

                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (req.Method == HttpMethod.Delete)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var client = new AcaDynamicSessionsClient(
                acaHttp,
                new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint },
                new DelegateTokenProvider(_ => Task.FromResult("plane2-integration-bearer")));
            await using var host = new AcaDynamicSessionsCandidateHost(
                runner,
                new ConfigurationBuilder().Build(),
                client,
                _ => Task.FromResult(workdir),
                healthPollInterval: TimeSpan.FromMilliseconds(10));

            var prepared = await host.Prepare(baseSha, "candidate change", new Dictionary<string, string>
            {
                ["app/Sample.txt"] = "updated content\n",
            }, Ct);
            Assert.True(Directory.Exists(prepared.Worktree));

            await host.BuildGate(prepared.Sha, Ct);
            var publishedDir = await host.Publish(prepared.Sha, Ct);
            Assert.EndsWith(prepared.Sha, publishedDir, StringComparison.Ordinal);

            var running = await host.Run(prepared.Sha, Ct);
            Assert.Equal(prepared.Sha, running.Sha);
            await host.WaitHealthy(prepared.Sha, TimeSpan.FromSeconds(5), Ct);

            var previous = await host.Commit(prepared.Sha, Ct);
            Assert.Null(previous);
            await host.Uncommit(prepared.Sha, previous, Ct);
            await host.Remove(prepared.Sha, Ct);

            Assert.Equal(["build-gate", "publish", "run-candidate"], seenActions.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Best-effort temp git repo cleanup.
                }
            }
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

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
}
