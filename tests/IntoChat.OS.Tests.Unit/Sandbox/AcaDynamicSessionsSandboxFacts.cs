using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace IntoChat.OS.Tests.Unit.Sandbox;

public sealed class AcaDynamicSessionsSandboxFacts
{
    private const string PoolEndpoint =
        "https://eastus.dynamicsessions.io/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-intochat/sessionPools/pool-kata";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Options_DormantByDefault_AndEnforcesHttpsAndBounds()
    {
        var defaults = new AcaDynamicSessionsOptions();
        Assert.False(defaults.IsConfigured);
        defaults.Validate(); // Must never fail local/test boot when unconfigured.

        var sandboxDefaults = new SandboxOptions();
        Assert.True(sandboxDefaults.EnableLocalSandbox);
        Assert.False(sandboxDefaults.AcaDynamicSessions.IsConfigured);
        sandboxDefaults.Validate();

        var plaintext = new AcaDynamicSessionsOptions { PoolManagementEndpoint = "http://eastus.dynamicsessions.io/pool" };
        Assert.Throws<ArgumentException>(plaintext.Validate);

        var withQuery = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint + "?foo=bar" };
        Assert.Throws<ArgumentException>(withQuery.Validate);

        var withUserInfo = new AcaDynamicSessionsOptions { PoolManagementEndpoint = "https://user:pass@eastus.dynamicsessions.io/pool" };
        Assert.Throws<ArgumentException>(withUserInfo.Validate);

        var zeroTimeout = new AcaDynamicSessionsOptions { ExecutionTimeout = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(zeroTimeout.Validate);

        var valid = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint };
        Assert.True(valid.IsConfigured);
        valid.Validate();
    }

    [Fact]
    public async Task ManagedIdentityTokenProvider_AcquiresCachesRefreshesAndFallsBackToImds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var msiCalls = 0;
        var seenHeaders = new ConcurrentQueue<(Uri Uri, string? IdentityHeader, string? MetadataHeader)>();

        using var msiHttp = new HttpClient(new StubHandler((req, _) =>
        {
            var call = Interlocked.Increment(ref msiCalls);
            seenHeaders.Enqueue((
                req.RequestUri!,
                req.Headers.TryGetValues("X-IDENTITY-HEADER", out var ih) ? ih.SingleOrDefault() : null,
                req.Headers.TryGetValues("Metadata", out var md) ? md.SingleOrDefault() : null));
            var expiresOn = clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
            {
                access_token = $"entra-token-{call}",
                expires_on = expiresOn.ToString(),
            }));
        }));

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["IDENTITY_ENDPOINT"] = "http://127.0.0.1:42356/msi/token",
            ["IDENTITY_HEADER"] = "msi-local-secret-header",
        };
        using var provider = new ManagedIdentityAcaTokenProvider(
            msiHttp,
            key => env.TryGetValue(key, out var val) ? val : null,
            () => "uami-client-id-123",
            clock);

        var t1 = await provider.AcquireTokenAsync(AcaDynamicSessionsOptions.DefaultTokenScope, Ct);
        var t2 = await provider.AcquireTokenAsync(AcaDynamicSessionsOptions.DefaultTokenScope, Ct);
        Assert.Equal("entra-token-1", t1);
        Assert.Equal("entra-token-1", t2);
        Assert.Equal(1, msiCalls);

        // Advance past refresh margin (10m expiry - 2m margin = 8m).
        clock.Advance(TimeSpan.FromMinutes(9));
        var t3 = await provider.AcquireTokenAsync(AcaDynamicSessionsOptions.DefaultTokenScope, Ct);
        Assert.Equal("entra-token-2", t3);
        Assert.Equal(2, msiCalls);

        Assert.True(seenHeaders.TryDequeue(out var firstReq));
        Assert.Equal("msi-local-secret-header", firstReq.IdentityHeader);
        Assert.Contains("resource=https%3A%2F%2Fdynamicsessions.io", firstReq.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("client_id=uami-client-id-123", firstReq.Uri.Query, StringComparison.Ordinal);

        // Verify IMDS fallback when IDENTITY_ENDPOINT is absent.
        using var imdsProvider = new ManagedIdentityAcaTokenProvider(
            msiHttp,
            _ => null,
            () => null,
            clock);
        var imdsToken = await imdsProvider.AcquireTokenAsync(AcaDynamicSessionsOptions.DefaultTokenScope, Ct);
        Assert.Equal("entra-token-3", imdsToken);
        var lastReq = seenHeaders.Last();
        Assert.Equal("169.254.169.254", lastReq.Uri.Host);
        Assert.Equal("true", lastReq.MetadataHeader);
    }

    [Fact]
    public void SessionIsolation_GeneratesDistinctSafeIds_AndRejectsUnsafeInputs()
    {
        var s1 = AcaSessionIsolation.BuildPlane1SessionId("brain/prod:1", "orch.app/v1");
        var s2 = AcaSessionIsolation.BuildPlane1SessionId("brain/prod:1", "orch.app/v1");

        Assert.NotEqual(s1, s2);
        Assert.StartsWith("p1-", s1, StringComparison.Ordinal);
        Assert.True(s1.Length <= AcaSessionIsolation.MaxSessionIdLength);
        Assert.True(s2.Length <= AcaSessionIsolation.MaxSessionIdLength);

        Assert.Throws<ArgumentException>(() => AcaSessionIsolation.ValidateSessionId("../escape"));
        Assert.Throws<ArgumentException>(() => AcaSessionIsolation.ValidateSessionId("bad/session"));
        Assert.Throws<ArgumentException>(() => AcaSessionIsolation.ValidateSessionId("bad?identifier=other"));
        Assert.Throws<ArgumentException>(() => AcaSessionIsolation.ValidateSessionId(new string('a', 65)));
    }

    [Fact]
    public async Task Plane1Sandbox_FailsClosedWhenUnconfigured_WithZeroNetworkCalls()
    {
        var requests = 0;
        var tokenCalls = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var sandboxOptions = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = TestSigningMaterial(),
            AcaDynamicSessions = new AcaDynamicSessionsOptions { PoolManagementEndpoint = null },
        });
        var client = new AcaDynamicSessionsClient(
            http,
            sandboxOptions.Value.AcaDynamicSessions,
            new DelegateTokenProvider(_ =>
            {
                Interlocked.Increment(ref tokenCalls);
                return Task.FromResult("should-never-be-acquired");
            }));
        var sandbox = new AcaDynamicSessionsSandbox(
            client,
            sandboxOptions,
            new AppTokenService(sandboxOptions),
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        Assert.False(sandbox.IsConfigured);

        await Assert.ThrowsAsync<AcaDynamicSessionsNotConfiguredException>(() =>
            sandbox.ExecuteScopedCommandAsync(
                new BrainId("brain-a"),
                CreateSamplePackage("orch-1"),
                [1, 2, 3],
                "orchestrate",
                "{}",
                ["notifications"],
                Ct));

        Assert.Equal(0, requests);
        Assert.Equal(0, tokenCalls);
    }

    [Fact]
    public async Task Plane1Sandbox_ExecutesOverDynamicSessionsApi_WithBearerToken_SessionIsolation_ZeroSecretLeakage_AndTeardown()
    {
        const string bearerToken = "eyJhbGciOiJSUzI1NiJ9.aca-bearer-secret-token-value.sig";
        const string enrollKey = "super-secret-enroll-key-never-send";
        const string signingMaterial = "super-secret-hmac-signing-material-never-send";

        var recorded = new ConcurrentQueue<CapturedRequest>();
        using var http = new HttpClient(new StubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
            recorded.Enqueue(new CapturedRequest(
                req.Method,
                req.RequestUri!,
                req.Headers.Authorization?.Scheme,
                req.Headers.Authorization?.Parameter,
                req.Headers.TryGetValues("X-IntoChat-Session-Id", out var sid) ? sid.SingleOrDefault() : null,
                body));

            if (req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return JsonResponse(HttpStatusCode.OK, new
            {
                status = "Succeeded",
                result = new
                {
                    handled = true,
                    resultJson = """{"status":"succeeded","summary":"Executed in Kata MicroVM"}""",
                },
            });
        }));

        var acaOptions = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint };
        var sandboxOptions = Options.Create(new SandboxOptions
        {
            EnrollKey = enrollKey,
            TokenSigningMaterial = signingMaterial,
            KernelCallbackUrl = "http://127.0.0.1:5080", // Loopback must never leak to cloud Kata MicroVM.
            AcaDynamicSessions = acaOptions,
        });
        var tokens = new AppTokenService(sandboxOptions);
        var client = new AcaDynamicSessionsClient(http, acaOptions, new DelegateTokenProvider(_ => Task.FromResult(bearerToken)));
        var sandbox = new AcaDynamicSessionsSandbox(
            client,
            sandboxOptions,
            tokens,
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        var r1 = await sandbox.ExecuteScopedCommandAsync(
            new BrainId("brain-alpha"),
            CreateSamplePackage("orch-alpha"),
            Encoding.UTF8.GetBytes("compiled-assembly-bytes"),
            "orchestrate",
            """{"n":7}""",
            ["notifications"],
            Ct);
        var session1 = sandbox.LastSessionId!;

        var r2 = await sandbox.ExecuteScopedCommandAsync(
            new BrainId("brain-alpha"),
            CreateSamplePackage("orch-alpha"),
            Encoding.UTF8.GetBytes("compiled-assembly-bytes"),
            "orchestrate",
            """{"n":8}""",
            ["notifications"],
            Ct);
        var session2 = sandbox.LastSessionId!;

        Assert.True(r1.Handled);
        Assert.Equal("aca-dynamic-sessions", r1.ExecutionTarget);
        Assert.Null(r1.ExecutedByMachineId);
        Assert.Contains("Executed in Kata MicroVM", r1.ResultJson, StringComparison.Ordinal);
        Assert.True(r2.Handled);
        Assert.NotEqual(session1, session2);

        var allRequests = recorded.ToArray();
        Assert.Equal(4, allRequests.Length); // 2x POST /executions + 2x DELETE /sessions/{id}

        var execRequests = allRequests.Where(r => r.Method == HttpMethod.Post).ToArray();
        var deleteRequests = allRequests.Where(r => r.Method == HttpMethod.Delete).ToArray();
        Assert.Equal(2, execRequests.Length);
        Assert.Equal(2, deleteRequests.Length);

        foreach (var req in allRequests)
        {
            Assert.Equal("Bearer", req.AuthScheme);
            Assert.Equal(bearerToken, req.AuthParameter);
            Assert.Contains("api-version=2024-02-02-preview", req.Uri.Query, StringComparison.Ordinal);
        }

        Assert.Contains($"/executions?identifier={session1}", execRequests[0].Uri.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains($"/executions?identifier={session2}", execRequests[1].Uri.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains($"/sessions/{session1}", deleteRequests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains($"/sessions/{session2}", deleteRequests[1].Uri.AbsolutePath, StringComparison.Ordinal);

        // Zero secret leakage assertions on outbound payload.
        foreach (var exec in execRequests)
        {
            Assert.DoesNotContain(enrollKey, exec.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(signingMaterial, exec.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(bearerToken, exec.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", exec.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("pipe://", exec.Body, StringComparison.Ordinal);
            Assert.Contains("\"allowedContracts\":[\"notifications\"]", exec.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Plane1Sandbox_RedactsSecretsFromManagementPlaneErrors_AndEnforcesTimeoutAndCancellation()
    {
        var bearerToken = string.Concat("eyJ.", "secret-bearer-token-leaked-by-upstream", ".sig");
        var leakedPat = string.Concat("ghp_", new string('a', 24));

        // 1. Secret redaction on upstream HTTP 500 error.
        using var errorHttp = new HttpClient(new StubHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"Upstream failure echoing Authorization: Bearer {bearerToken} and {leakedPat}"),
            });
        }));

        var errorOpts = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint };
        var sandboxOpts = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = TestSigningMaterial(),
            AcaDynamicSessions = errorOpts,
        });
        var errorSandbox = new AcaDynamicSessionsSandbox(
            new AcaDynamicSessionsClient(errorHttp, errorOpts, new DelegateTokenProvider(_ => Task.FromResult(bearerToken))),
            sandboxOpts,
            new AppTokenService(sandboxOpts),
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        var ex = await Assert.ThrowsAsync<AcaDynamicSessionsException>(() =>
            errorSandbox.ExecuteScopedCommandAsync(
                new BrainId("brain-err"),
                CreateSamplePackage("orch-err"),
                [1, 2],
                "orchestrate",
                "{}",
                ["notifications"],
                Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain(bearerToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(leakedPat, ex.Message, StringComparison.Ordinal);
        Assert.Contains(AcaPayloadSecretScrubber.RedactedMarker, ex.Message, StringComparison.Ordinal);

        // 2. Strict execution timeout -> TimeoutException.
        using var slowHttp = new HttpClient(new StubHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var timeoutOpts = new AcaDynamicSessionsOptions
        {
            PoolManagementEndpoint = PoolEndpoint,
            ExecutionTimeout = TimeSpan.FromMilliseconds(120),
        };
        var timeoutSandbox = new AcaDynamicSessionsSandbox(
            new AcaDynamicSessionsClient(slowHttp, timeoutOpts, new DelegateTokenProvider(_ => Task.FromResult("tok"))),
            Options.Create(new SandboxOptions { TokenSigningMaterial = TestSigningMaterial(), AcaDynamicSessions = timeoutOpts }),
            new AppTokenService(sandboxOpts),
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            timeoutSandbox.ExecuteScopedCommandAsync(
                new BrainId("brain-slow"),
                CreateSamplePackage("orch-slow"),
                [1, 2],
                "orchestrate",
                "{}",
                ["notifications"],
                Ct));

        // 3. Caller cancellation -> OperationCanceledException.
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(60));
        var longTimeoutOpts = new AcaDynamicSessionsOptions
        {
            PoolManagementEndpoint = PoolEndpoint,
            ExecutionTimeout = TimeSpan.FromSeconds(30),
        };
        var cancelSandbox = new AcaDynamicSessionsSandbox(
            new AcaDynamicSessionsClient(slowHttp, longTimeoutOpts, new DelegateTokenProvider(_ => Task.FromResult("tok"))),
            Options.Create(new SandboxOptions { TokenSigningMaterial = TestSigningMaterial(), AcaDynamicSessions = longTimeoutOpts }),
            new AppTokenService(sandboxOpts),
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelSandbox.ExecuteScopedCommandAsync(
                new BrainId("brain-cancel"),
                CreateSamplePackage("orch-cancel"),
                [1, 2],
                "orchestrate",
                "{}",
                ["notifications"],
                callerCts.Token));
    }

    [Fact]
    public async Task Plane1RoutingChain_PrefersSovereignClient_FallsBackToAcaWhenLocalDisabledOrMissing_AndFailsClosedWhenUnconfigured()
    {
        var acaExecutions = 0;
        using var acaHttp = new HttpClient(new StubHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref acaExecutions);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    status = "Succeeded",
                    result = new
                    {
                        handled = true,
                        resultJson = """{"status":"succeeded","summary":"ACA fallback ran"}""",
                    },
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));

        var services = new ServiceCollection();
        services.AddHttpClient();
        await using var sp = services.BuildServiceProvider();

        var acaOptions = new AcaDynamicSessionsOptions { PoolManagementEndpoint = PoolEndpoint };
        var disabledLocalOpts = Options.Create(new SandboxOptions
        {
            EnableLocalSandbox = false,
            TokenSigningMaterial = TestSigningMaterial(),
            AcaDynamicSessions = acaOptions,
        });
        var tokens = new AppTokenService(disabledLocalOpts);
        var acaSandbox = new AcaDynamicSessionsSandbox(
            new AcaDynamicSessionsClient(acaHttp, acaOptions, new DelegateTokenProvider(_ => Task.FromResult("aca-token"))),
            disabledLocalOpts,
            tokens,
            NullLogger<AcaDynamicSessionsSandbox>.Instance);

        // Case 1: Online consented sovereign client wins; ACA is NOT invoked.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var transport = new StubComputeTransport(respond: true);
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);
        var brain = new BrainId("brain-chain");
        await router.RecordHeartbeatAsync(
            brain,
            new ComputeCapabilityAdvertisement(
                MachineId: "desktop-1",
                DeviceName: "Owner PC",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow(),
                AllowClientCodeSandbox: true,
                AdvertisedCapabilities: [ICodeSandbox.CapabilityId]),
            Ct);

        await using var bridge1 = new HttpSandboxRuntimeBridge(
            sp.GetRequiredService<IHttpClientFactory>(),
            disabledLocalOpts,
            tokens,
            sp,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router,
            acaSandbox);

        var sovereignResult = await bridge1.ExecuteScopedCommandAsync(
            brain, CreateSamplePackage("orch-1"), [1, 2], "orchestrate", "{}", ["notifications"], Ct);
        Assert.Equal("connected-client", sovereignResult.ExecutionTarget);
        Assert.Equal("desktop-1", sovereignResult.ExecutedByMachineId);
        Assert.Equal(0, acaExecutions);

        // Case 2: Sovereign client goes offline + local sandbox disabled -> falls back to ACA Dynamic Sessions.
        await router.MarkOfflineAsync(brain, "desktop-1", Ct);
        var acaResult = await bridge1.ExecuteScopedCommandAsync(
            brain, CreateSamplePackage("orch-2"), [1, 2], "orchestrate", "{}", ["notifications"], Ct);
        Assert.Equal("aca-dynamic-sessions", acaResult.ExecutionTarget);
        Assert.True(acaResult.Handled);
        Assert.Equal(1, acaExecutions);

        // Case 3: Local sandbox enabled but binary missing -> transparently falls back to ACA Dynamic Sessions.
        var missingLocalOpts = Options.Create(new SandboxOptions
        {
            EnableLocalSandbox = true,
            UnitHostAssemblyPath = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"), "IntoChat.OS.Sandbox.dll"),
            TokenSigningMaterial = TestSigningMaterial(),
            AcaDynamicSessions = acaOptions,
        });
        await using var bridge2 = new HttpSandboxRuntimeBridge(
            sp.GetRequiredService<IHttpClientFactory>(),
            missingLocalOpts,
            tokens,
            sp,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router,
            acaSandbox);

        var missingLocalFallback = await bridge2.ExecuteScopedCommandAsync(
            brain, CreateSamplePackage("orch-3"), [1, 2], "orchestrate", "{}", ["notifications"], Ct);
        Assert.Equal("aca-dynamic-sessions", missingLocalFallback.ExecutionTarget);
        Assert.Equal(2, acaExecutions);

        // Case 4: Sovereign client offline + local sandbox disabled + ACA unconfigured -> fails closed.
        var unconfiguredOpts = Options.Create(new SandboxOptions
        {
            EnableLocalSandbox = false,
            TokenSigningMaterial = TestSigningMaterial(),
            AcaDynamicSessions = new AcaDynamicSessionsOptions { PoolManagementEndpoint = null },
        });
        var dormantAca = new AcaDynamicSessionsSandbox(
            new AcaDynamicSessionsClient(acaHttp, unconfiguredOpts.Value.AcaDynamicSessions, new DelegateTokenProvider(_ => Task.FromResult("unused"))),
            unconfiguredOpts,
            tokens,
            NullLogger<AcaDynamicSessionsSandbox>.Instance);
        await using var bridge3 = new HttpSandboxRuntimeBridge(
            sp.GetRequiredService<IHttpClientFactory>(),
            unconfiguredOpts,
            tokens,
            sp,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router,
            dormantAca);

        await Assert.ThrowsAsync<AcaDynamicSessionsNotConfiguredException>(() =>
            bridge3.ExecuteScopedCommandAsync(
                brain, CreateSamplePackage("orch-4"), [1, 2], "orchestrate", "{}", ["notifications"], Ct));
    }

    private static string TestSigningMaterial() => new('t', 32);

    private static AppPackage CreateSamplePackage(string key) => new(
        Key: key,
        Version: "1.0.0",
        Title: key,
        Summary: "Test orchestration script",
        Icon: "terminal",
        Category: "orchestration",
        Author: "test",
        SourceFiles: [new AppSourceFile("Program.cs", "// test")],
        Needs: AppPackageNeeds.Empty,
        DefaultSettingsJson: "{}",
        Digest: "sha256:test");

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
        string? SessionHeader,
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

    private sealed class StubComputeTransport(bool respond) : IConnectedComputeTransport
    {
        public Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
            => Task.FromResult(respond);

        public Task<ConnectedSandboxResponse?> ExecuteAsync(
            BrainId brainId,
            ConnectedComputeNode node,
            ConnectedSandboxRequest request,
            CancellationToken ct = default)
            => Task.FromResult<ConnectedSandboxResponse?>(respond
                ? new ConnectedSandboxResponse(true, """{"status":"succeeded","summary":"Ran on desktop"}""", null, null, node.MachineId)
                : null);
    }
}
