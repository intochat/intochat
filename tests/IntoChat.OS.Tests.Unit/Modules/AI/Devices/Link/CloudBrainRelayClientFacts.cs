using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.External;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class CloudBrainRelayClientFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnconfiguredClientReportsFalseAndThrowsDeviceUnavailable()
    {
        using var root = new TempDirectory("intochat-brain-relay-unconfigured-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new PlaintextProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var http = new HttpClient(handler);
        var client = new CloudBrainRelayClient(http, new CloudClient(http, credentials, clock), credentials, signer: null);

        Assert.False(await client.IsConfiguredAsync(Ct));
        var err = await Assert.ThrowsAsync<DeviceUnavailableException>(() => client.BrainModelsAsync("ada", Ct));
        Assert.Equal("relay-unconfigured", err.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DeviceLinkTakesPrecedenceOverServiceAssertionAndFallsBackWhenRevoked()
    {
        using var root = new TempDirectory("intochat-brain-relay-precedence-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new PlaintextProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await credentials.Write(new LinkSecrets(
            new Uri("https://device-link.test/"),
            "cred-1",
            "device-access-token",
            clock.GetUtcNow().AddHours(1),
            "device-refresh-token",
            clock.GetUtcNow().AddDays(30),
            deviceKey.ExportPkcs8PrivateKey()), Ct);

        using var serviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = CreateSigner(serviceKey, "https://hosted-service.test/", clock);

        using var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new BrainComputeNodesResponse("ada", []), DeviceWireJson.Default.BrainComputeNodesResponse),
                Encoding.UTF8,
                "application/json"),
        }));
        using var http = new HttpClient(handler);
        var client = new CloudBrainRelayClient(http, new CloudClient(http, credentials, clock), credentials, signer);

        Assert.True(await client.IsConfiguredAsync(Ct));

        // 1. With link.bin present, device-link Bearer + X-Machine-Proof wins over ServiceAssertion.
        var first = await client.BrainComputeNodesAsync("ada", Ct);
        Assert.NotNull(first);
        var firstReq = Assert.Single(handler.Requests);
        Assert.Equal("https://device-link.test" + DeviceRoutes.ComputeNodes + "?brain=ada", firstReq.Uri.AbsoluteUri);
        Assert.Equal("Bearer", firstReq.AuthScheme);
        Assert.Equal("device-access-token", firstReq.AuthParameter);
        Assert.False(string.IsNullOrEmpty(firstReq.MachineProof));

        // 2. Once link.bin is revoked, the same client seamlessly falls back to ServiceAssertion.
        await credentials.Revoke(Ct);
        Assert.True(await client.IsConfiguredAsync(Ct));

        var second = await client.BrainComputeNodesAsync("ada", Ct);
        Assert.NotNull(second);
        Assert.Equal(2, handler.Requests.Count);
        var secondReq = handler.Requests[1];
        Assert.Equal("https://hosted-service.test" + DeviceRoutes.ComputeNodes + "?brain=ada", secondReq.Uri.AbsoluteUri);
        Assert.Equal(ServiceAssertion.Scheme, secondReq.AuthScheme);
        Assert.Equal(string.Empty, secondReq.MachineProof);

        var verified = ServiceAssertion.Verify(
            secondReq.AuthParameter,
            issuer => issuer == "os.test.intochat.io" ? (serviceKey, "https://hosted-service.test") : null,
            clock);
        Assert.NotNull(verified);
        Assert.Equal("ada", verified!.Subject);
        Assert.Equal(CloudBrainRelayClient.ComputeDispatchPurpose, verified.Purpose);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task DispatchMapsUnavailableStatusesToNullForBothSandboxAndResource(HttpStatusCode status)
    {
        using var root = new TempDirectory("intochat-brain-relay-status-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new PlaintextProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var serviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = CreateSigner(serviceKey, "https://hosted-service.test/", clock);

        using var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        var client = new CloudBrainRelayClient(http, new CloudClient(http, credentials, clock), credentials, signer);

        var sandbox = await client.DispatchBrainSandboxAsync(
            new BrainSandboxDispatchRequest(
                "ada",
                "pc-1",
                new ConnectedSandboxRequest("job-1", "ada", "app", "1.0", "sha256:x", "AQID", "run", "{}", null, null, "https://k", "tok", [])),
            TimeSpan.FromSeconds(5),
            Ct);
        var resource = await client.DispatchBrainResourceAsync(
            new BrainResourceDispatchRequest(
                "ada",
                "pc-1",
                new PcResourceRequest("job-2", "ada", ClientComputeConsentPolicy.WindowsFolderGatewayCapability, "list", "{}")),
            TimeSpan.FromSeconds(5),
            Ct);

        Assert.Null(sandbox);
        Assert.Null(resource);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task BrainComputeNodesMapsNotFoundAndNotImplementedToNull(HttpStatusCode status)
    {
        using var root = new TempDirectory("intochat-brain-relay-nodes-status-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new PlaintextProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var serviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = CreateSigner(serviceKey, "https://hosted-service.test/", clock);

        using var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        var client = new CloudBrainRelayClient(http, new CloudClient(http, credentials, clock), credentials, signer);

        Assert.Null(await client.BrainComputeNodesAsync("ada", Ct));
    }

    [Fact]
    public async Task DispatchCancelsWhenAvailabilityTimeoutElapses()
    {
        using var root = new TempDirectory("intochat-brain-relay-timeout-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new PlaintextProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var serviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = CreateSigner(serviceKey, "https://hosted-service.test/", clock);

        using var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var client = new CloudBrainRelayClient(http, new CloudClient(http, credentials, clock), credentials, signer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DispatchBrainSandboxAsync(
            new BrainSandboxDispatchRequest(
                "ada",
                "pc-1",
                new ConnectedSandboxRequest("job-to", "ada", "app", "1.0", "sha256:x", "AQID", "run", "{}", null, null, "https://k", "tok", [])),
            TimeSpan.FromMilliseconds(50),
            Ct));
    }

    [Fact]
    public async Task SharedSignerPreservesKernelRelayServiceAssertionFirstAndBearerFallbackSemantics()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        using var serviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = CreateSigner(serviceKey, "https://relay.test/", clock);

        // 1. ServiceAssertion wins when signer is configured and brainId is present.
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://relay.test/circle/v1/brains/send"))
        {
            Content = new StringContent("""{"hello":"world"}""", Encoding.UTF8, "application/json"),
        })
        {
            await CloudRelayRequestSigner.AuthorizeServiceOrBearerAsync(
                request,
                signer,
                brainId: "alice",
                purpose: KernelBrainRelayClientProvider.RelayPurpose,
                bearerToken: "fallback-bearer",
                machineProofFactory: static (_, _, _) => ValueTask.FromResult<string?>("should-not-be-used"),
                ct: Ct);

            Assert.Equal(ServiceAssertion.Scheme, request.Headers.Authorization?.Scheme);
            Assert.False(request.Headers.Contains(CircleRoutes.MachineProofHeader));
            var verified = ServiceAssertion.Verify(
                request.Headers.Authorization?.Parameter,
                issuer => issuer == "os.test.intochat.io" ? (serviceKey, "https://relay.test") : null,
                clock);
            Assert.NotNull(verified);
            Assert.Equal("alice", verified!.Subject);
            Assert.Equal(KernelBrainRelayClientProvider.RelayPurpose, verified.Purpose);
        }

        // 2. Falls back to Bearer + X-Machine-Proof when brainId is missing or signer is null.
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://relay.test/circle/v1/brains/send"))
        {
            Content = new StringContent("""{"hello":"world"}""", Encoding.UTF8, "application/json"),
        })
        {
            byte[]? capturedBody = null;
            await CloudRelayRequestSigner.AuthorizeServiceOrBearerAsync(
                request,
                signer,
                brainId: null,
                purpose: KernelBrainRelayClientProvider.RelayPurpose,
                bearerToken: "desktop-bearer",
                machineProofFactory: (_, body, _) =>
                {
                    capturedBody = body;
                    return ValueTask.FromResult<string?>("machine-proof-token");
                },
                ct: Ct);

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("desktop-bearer", request.Headers.Authorization?.Parameter);
            Assert.Equal("machine-proof-token", Assert.Single(request.Headers.GetValues(CircleRoutes.MachineProofHeader)));
            Assert.Equal("""{"hello":"world"}""", Encoding.UTF8.GetString(capturedBody!));
        }
    }

    private static ServiceAssertionSigner CreateSigner(ECDsa key, string serviceUrl, TimeProvider clock)
        => new(
            Options.Create(new AuthOptions
            {
                ServiceSigner = new ServiceSignerOptions
                {
                    Issuer = "os.test.intochat.io",
                    Audience = serviceUrl.TrimEnd('/'),
                    ServiceUrl = serviceUrl,
                    PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
                },
            }),
            clock);

    private sealed record RecordedRequest(Uri Uri, string? AuthScheme, string? AuthParameter, string MachineProof);

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
            : this((req, _) => respond(req))
        {
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var proof = request.Headers.TryGetValues(CircleRoutes.MachineProofHeader, out var values) ? values.Single() : string.Empty;
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, proof));
            return respond(request, cancellationToken);
        }
    }

    private sealed class PlaintextProtector : IMachineKeyProtector
    {
        public byte[] Protect(byte[] key) => key;
        public byte[] Unprotect(byte[] stored) => stored;
    }
}
