using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Http;
using IntoChat.OS.Kernel.Sandbox;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Sandbox;

public sealed class SandboxCollaboratorsFacts
{
    [Fact]
    public async Task HttpSandboxWorkerClientRetriesTransientFailuresAttachesEnrollKeyAndUsesAotJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;
        string? seenEnrollKey = null;
        string? seenPath = null;
        string? seenBody = null;

        using var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("Worker starting up");
            }

            seenPath = request.RequestUri?.AbsolutePath;
            seenEnrollKey = request.Headers.TryGetValues("X-Sandbox-Enroll-Key", out var values)
                ? values.FirstOrDefault()
                : null;
            seenBody = await request.Content!.ReadAsStringAsync(token);

            var payload = JsonSerializer.Serialize(
                new DispatchWorkerResponse(true, "{\"ok\":true}", "{\"step\":2}", null, null),
                SandboxEdgeJsonContext.Default.DispatchWorkerResponse);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        });

        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var factory = new SingleClientFactory(httpClient);
        var options = Options.Create(new SandboxOptions
        {
            Url = "http://sandbox-worker.local:8080",
            EnrollKey = "enroll-secret-42",
        });
        await using var services = new ServiceCollection().BuildServiceProvider();

        var client = new HttpSandboxWorkerClient(
            factory,
            options,
            services,
            NullLogger.Instance,
            maxAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(1));

        var response = await client.DispatchAsync(
            "http://sandbox-worker.local:8080/",
            new DispatchWorkerRequest("brain-1", "demo", "command", "ping", "{}", null, null, null, "tok"),
            ct);

        Assert.Equal(2, attempts);
        Assert.Equal("/apps/dispatch", seenPath);
        Assert.Equal("enroll-secret-42", seenEnrollKey);
        Assert.Contains("\"appKey\":\"demo\"", seenBody, StringComparison.Ordinal);
        Assert.True(response.Handled);
        Assert.Equal("{\"ok\":true}", response.ResultJson);
        Assert.Equal("{\"step\":2}", response.UpdatedStateJson);
    }

    [Fact]
    public async Task HttpSandboxWorkerClientThrowsOnNonSuccessStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("worker unavailable"),
            }));
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var options = Options.Create(new SandboxOptions { Url = "http://sandbox-worker.local:8080" });
        await using var services = new ServiceCollection().BuildServiceProvider();

        var client = new HttpSandboxWorkerClient(
            new SingleClientFactory(httpClient),
            options,
            services,
            NullLogger.Instance,
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.UnloadAsync(
            "http://sandbox-worker.local:8080",
            new UnloadWorkerRequest("brain-1", "demo"),
            ct));

        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
        Assert.Contains("worker unavailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxCallbackProxyHandlesLocalCallbacksAndEnforcesAuthAndRemoteSsrf()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "callback-proxy-test-signing-material-0123456789",
            AllowLoopbackKernelCallback = false,
        });
        var tokens = new AppTokenService(options);
        await using var services = new ServiceCollection().BuildServiceProvider();

        var forwardedSignals = new List<(string BrainId, string SignalName, string PayloadJson)>();
        using var proxy = new SandboxCallbackProxy(
            options,
            tokens,
            services,
            (brainId, signalName, payloadJson, _) =>
            {
                forwardedSignals.Add((brainId, signalName, payloadJson));
                return Task.CompletedTask;
            },
            static (brainId, appKey) => $"{brainId}/app/{appKey}",
            NullLogger.Instance);

        var validToken = tokens.Issue("brain-1", "app-1");

        // Local signal with valid token succeeds and forwards.
        var signalReply = await proxy.HandlePipeCallbackAsync(
            new UnitHostFrameResponse(
                Id: "f1",
                Ok: true,
                CallbackKind: "signal",
                BrainId: "brain-1",
                AppKey: "app-1",
                AppToken: validToken,
                Signal: new SandboxEdgeEndpoints.EdgeSignalRequest("app.ready", "{\"v\":1}")),
            ct);
        Assert.True(signalReply.Ok);
        Assert.Equal(StatusCodes.Status200OK, signalReply.StatusCode);
        Assert.Single(forwardedSignals);
        Assert.Equal(("brain-1", "app.ready", "{\"v\":1}"), forwardedSignals[0]);

        // Local log with bad token fails with 401.
        var unauthLog = await proxy.HandlePipeCallbackAsync(
            new UnitHostFrameResponse(
                Id: "f2",
                Ok: true,
                CallbackKind: "log",
                BrainId: "brain-1",
                AppKey: "app-1",
                AppToken: "invalid-token",
                Log: new SandboxEdgeEndpoints.EdgeLogRequest("hello", DateTimeOffset.UtcNow)),
            ct);
        Assert.False(unauthLog.Ok);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthLog.StatusCode);

        // Unsupported callback kind returns 404.
        var unknownReply = await proxy.HandlePipeCallbackAsync(
            new UnitHostFrameResponse(Id: "f3", Ok: true, CallbackKind: "unknown"),
            ct);
        Assert.False(unknownReply.Ok);
        Assert.Equal(StatusCodes.Status404NotFound, unknownReply.StatusCode);

        // Remote job with metadata IP is rejected and fails closed with 403.
        var regId = proxy.RegisterForRemoteJob(
            "brain-1/app/app-1",
            "job-1",
            "brain-1",
            "app-1",
            "http://169.254.169.254/latest/meta-data");
        Assert.NotNull(regId);
        try
        {
            var blockedReply = await proxy.HandlePipeCallbackAsync(
                new UnitHostFrameResponse(
                    Id: "f4",
                    Ok: true,
                    CallbackKind: "invoke",
                    BrainId: "brain-1",
                    AppKey: "app-1",
                    AppToken: validToken,
                    Invoke: new SandboxEdgeEndpoints.EdgeInvokeRequest("IFiles", "Read", null, "[]")),
                ct);
            Assert.False(blockedReply.Ok);
            Assert.Equal(StatusCodes.Status403Forbidden, blockedReply.StatusCode);
            Assert.NotNull(blockedReply.InvokeResult?.Error);
        }
        finally
        {
            proxy.UnregisterRemoteCallback("brain-1/app/app-1", regId!.Value);
        }
    }

    [Fact]
    public async Task SandboxCallbackProxyConvertsHostNotAllowedAtConnectTimeTo403()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "callback-proxy-test-signing-material-0123456789",
            AllowLoopbackKernelCallback = false,
        });
        var tokens = new AppTokenService(options);
        await using var services = new ServiceCollection().BuildServiceProvider();

        using var blockingHandler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("blocked", new HostNotAllowedException("edge.example.test")));
        using var proxy = new SandboxCallbackProxy(
            options,
            tokens,
            services,
            static (_, _, _, _) => Task.CompletedTask,
            static (brainId, appKey) => $"{brainId}/app/{appKey}",
            NullLogger.Instance,
            remoteCallbackClientFactory: () => new HttpClient(blockingHandler, disposeHandler: false));

        var regId = proxy.RegisterForRemoteJob(
            "brain-1/app/app-1",
            "job-2",
            "brain-1",
            "app-1",
            "https://edge.example.test");
        Assert.NotNull(regId);
        try
        {
            var reply = await proxy.HandlePipeCallbackAsync(
                new UnitHostFrameResponse(
                    Id: "f5",
                    Ok: true,
                    CallbackKind: "invoke",
                    BrainId: "brain-1",
                    AppKey: "app-1",
                    AppToken: "tok",
                    Invoke: new SandboxEdgeEndpoints.EdgeInvokeRequest("IFiles", "Read", null, "[]")),
                ct);
            Assert.False(reply.Ok);
            Assert.Equal(StatusCodes.Status403Forbidden, reply.StatusCode);
            Assert.Contains("edge.example.test", reply.Error, StringComparison.Ordinal);
        }
        finally
        {
            proxy.UnregisterRemoteCallback("brain-1/app/app-1", regId!.Value);
        }
    }

    [Fact]
    public async Task UnitHostTransportValidatesConfiguredPathAndDisposesCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<FileNotFoundException>(() => UnitHostTransport.ResolveAssemblyPath(
            Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".dll")));

        var options = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "unithost-transport-test-signing-material-0123456789",
        });
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var proxy = new SandboxCallbackProxy(
            options,
            new AppTokenService(options),
            services,
            static (_, _, _, _) => Task.CompletedTask,
            static (brainId, appKey) => $"{brainId}/app/{appKey}",
            NullLogger.Instance);
        var transport = new UnitHostTransport(options, services, proxy, NullLogger.Instance);

        await transport.DisposeAsync();
        await transport.DisposeAsync(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transport.SendAsync(new UnitHostFrameRequest("f1", "ping"), ct));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }
}
