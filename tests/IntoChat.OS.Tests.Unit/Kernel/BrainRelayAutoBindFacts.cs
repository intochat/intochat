using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Kernel;

public sealed class BrainRelayAutoBindFacts
{
    private sealed class PassthroughKeyProtector : IMachineKeyProtector
    {
        public byte[] Protect(byte[] plain) => (byte[])plain.Clone();
        public byte[] Unprotect(byte[] stored) => (byte[])stored.Clone();
    }

    private sealed class CapturingHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    private sealed class MemoryNonceCache : INonceCache
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public bool TryAdd(string jti, DateTimeOffset expires) => _seen.Add(jti);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }

    private static async Task<(DeviceCredentials Credentials, byte[] PublicKeySpki, string TempDir)> CreateLinkedCredentialsAsync(
        Uri server,
        string accessToken,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "intochat-relay-autobind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "link.bin");
        var creds = new DeviceCredentials(new PassthroughKeyProtector(), path);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var spki = key.ExportSubjectPublicKeyInfo();

        await creds.Write(new LinkSecrets(
            Server: server,
            CredentialId: "cred-desktop-1",
            AccessToken: accessToken,
            AccessExpiresAt: expiresAt,
            RefreshToken: "refresh-secret-never-logged",
            RefreshExpiresAt: expiresAt.AddDays(30),
            PrivateKeyPkcs8: pkcs8), ct);

        return (creds, spki, dir);
    }

    [Fact]
    public async Task AutoBindsLinkBinServerAndMachineProofWithoutCopyingSecretsIntoOptionsOrLogs()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var cloudUri = new Uri("https://cloud.intochat.test");
        const string accessToken = "desktop-access-secret-xyz";
        var (creds, spki, tempDir) = await CreateLinkedCredentialsAsync(cloudUri, accessToken, clock.GetUtcNow().AddHours(1), ct);

        try
        {
            string? capturedAuth = null;
            string? capturedProof = null;
            Uri? capturedUri = null;
            string? capturedRecipientsHeader = null;

            var handler = new CapturingHttpHandler(async (req, token) =>
            {
                capturedAuth = req.Headers.Authorization?.ToString();
                capturedProof = req.Headers.TryGetValues(CircleRoutes.MachineProofHeader, out var vals) ? vals.Single() : null;
                capturedUri = req.RequestUri;
                capturedRecipientsHeader = req.Headers.TryGetValues(BrainRelayTransportHosting.CircleBlobRecipientsHeader, out var r)
                    ? r.Single()
                    : null;

                var body = req.Content is not null ? await req.Content.ReadAsByteArrayAsync(token) : [];
                var sha = Convert.ToHexStringLower(SHA256.HashData(body));
                var payload = JsonSerializer.Serialize(new
                {
                    blobId = "blob-1",
                    sha256 = sha,
                    sizeBytes = body.Length,
                    expiresAt = clock.GetUtcNow().AddDays(7),
                });
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
            });

            var loggerProvider = new CapturingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(creds);
            services.AddSingleton(sp => new CloudClient(new HttpClient(handler), creds, clock));
            services.AddSingleton<IDeviceLinkCredentialsProvider>(sp =>
                new DeviceLinkCredentialsProvider(creds, sp.GetRequiredService<CloudClient>(), clock));
            services.AddKernelBrainRelayTransport();
            services.AddHttpClient(BrainRelayTransportHosting.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();

            var opts = provider.GetRequiredService<IOptions<BrainRelayOptions>>().Value;
            Assert.Equal(cloudUri, opts.RelayBaseUri);
            Assert.True(opts.EnablePollLoop);
            // Secrets MUST NOT be copied into plain BrainRelayOptions.
            Assert.Null(opts.BearerToken);

            var relayProvider = provider.GetRequiredService<KernelBrainRelayClientProvider>();
            Assert.True(relayProvider.IsConfigured);
            var client = relayProvider.TryGetClient();
            Assert.NotNull(client);

            var uploaded = await client!.UploadBlobAsync("ciphertext-blob"u8.ToArray(), [new BrainId("bob")], ct);
            Assert.Equal("blob-1", uploaded.BlobId);
            Assert.Equal("bob", capturedRecipientsHeader);
            Assert.Equal($"Bearer {accessToken}", capturedAuth);
            Assert.NotNull(capturedProof);
            Assert.NotNull(capturedUri);

            // Machine proof verifies cryptographically against the desktop machine's P-256 public key.
            var verify = MachineProof.Verify(
                spki,
                capturedProof!,
                "POST",
                capturedUri!,
                accessToken,
                clock.GetUtcNow(),
                new MemoryNonceCache());
            Assert.True(verify.IsValid, $"Expected valid machine proof, got {verify.Error}.");

            // Never leak secrets in logs.
            Assert.DoesNotContain(loggerProvider.Messages, m => m.Contains(accessToken, StringComparison.Ordinal));
            Assert.DoesNotContain(loggerProvider.Messages, m => m.Contains("refresh-secret-never-logged", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitConfigTakesPrecedenceOverLinkBinAutoBind()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var (creds, _, tempDir) = await CreateLinkedCredentialsAsync(
            new Uri("https://linkbin.intochat.test"),
            "linkbin-token",
            clock.GetUtcNow().AddHours(1),
            ct);

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IntoChatOS:BrainRelay:RelayBaseUri"] = "https://explicit.intochat.test",
                    ["IntoChatOS:BrainRelay:EnablePollLoop"] = "false",
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(creds);
            services.AddSingleton(new CloudClient(new HttpClient(), creds, clock));
            services.AddSingleton<IDeviceLinkCredentialsProvider>(sp =>
                new DeviceLinkCredentialsProvider(creds, sp.GetRequiredService<CloudClient>(), clock));
            services.AddKernelBrainRelayTransport();

            await using var provider = services.BuildServiceProvider();
            var opts = provider.GetRequiredService<IOptions<BrainRelayOptions>>().Value;

            Assert.Equal(new Uri("https://explicit.intochat.test"), opts.RelayBaseUri);
            Assert.False(opts.EnablePollLoop);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RevokedOrMissingLinkBinDegradesCleanlyToUnconfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var (creds, _, tempDir) = await CreateLinkedCredentialsAsync(
            new Uri("https://revoked.intochat.test"),
            "revoked-token",
            clock.GetUtcNow().AddHours(1),
            ct);

        try
        {
            await creds.Revoke(ct);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(creds);
            services.AddSingleton(new CloudClient(new HttpClient(), creds, clock));
            services.AddSingleton<IDeviceLinkCredentialsProvider>(sp =>
                new DeviceLinkCredentialsProvider(creds, sp.GetRequiredService<CloudClient>(), clock));
            services.AddKernelBrainRelayTransport();

            await using var provider = services.BuildServiceProvider();
            var opts = provider.GetRequiredService<IOptions<BrainRelayOptions>>().Value;
            var relayProvider = provider.GetRequiredService<KernelBrainRelayClientProvider>();

            Assert.Null(opts.RelayBaseUri);
            Assert.False(opts.EnablePollLoop);
            Assert.False(relayProvider.IsConfigured);
            Assert.Null(relayProvider.TryGetClient());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubServiceAssertionSigner(bool isConfigured, Uri? serviceUri) : IServiceAssertionSigner
    {
        public bool IsConfigured => isConfigured;
        public Uri? ServiceUri => serviceUri;

        public string Sign(string brainId, string purpose, string method, string pathAndQuery, ReadOnlySpan<byte> body)
            => $"signed-{brainId}-{purpose}";
    }

    [Fact]
    public async Task ServiceAssertionSignerPreventsLinkBinPostConfigureFromOverwritingRelayBaseUri()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var linkBinUri = new Uri("https://linkbin.intochat.test");
        var serviceUri = new Uri("https://service.intochat.test");
        var (creds, _, tempDir) = await CreateLinkedCredentialsAsync(
            linkBinUri,
            "linkbin-secret-token",
            clock.GetUtcNow().AddHours(1),
            ct);

        try
        {
            Uri? capturedUri = null;
            string? capturedAuth = null;
            var handler = new CapturingHttpHandler((req, _) =>
            {
                capturedUri = req.RequestUri;
                capturedAuth = req.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IntoChatOS:BrainRelay:LocalBrainId"] = "brain-cloud-1",
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IServiceAssertionSigner>(new StubServiceAssertionSigner(true, serviceUri));
            services.AddSingleton(creds);
            services.AddSingleton(new CloudClient(new HttpClient(handler), creds, clock));
            services.AddSingleton<IDeviceLinkCredentialsProvider>(sp =>
                new DeviceLinkCredentialsProvider(creds, sp.GetRequiredService<CloudClient>(), clock));
            services.AddKernelBrainRelayTransport();
            services.AddHttpClient(BrainRelayTransportHosting.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            var opts = provider.GetRequiredService<IOptions<BrainRelayOptions>>().Value;

            // B3: PostConfigure MUST NOT overwrite RelayBaseUri with link.bin when IServiceAssertionSigner is configured.
            Assert.Null(opts.RelayBaseUri);
            Assert.False(opts.EnablePollLoop);

            var relayProvider = provider.GetRequiredService<KernelBrainRelayClientProvider>();
            Assert.True(relayProvider.IsConfigured);
            var client = relayProvider.TryGetClient();
            Assert.NotNull(client);

            using var aliceKeys = BrainKeyPair.Generate();
            await using var os = DigitalBrain.Testing.TestOS.Local();
            var receiver = new BrainEnvelopeReceiver(
                new BrainId("brain-cloud-1"),
                aliceKeys,
                _ => null,
                os.Edge,
                os.InvokeLocalAsync,
                os.PostLocalAsync,
                clock: clock);

            await client!.PollOnceAsync(receiver, ct);

            Assert.NotNull(capturedUri);
            Assert.Equal("service.intochat.test", capturedUri!.Host);
            Assert.Equal("Service signed-brain-cloud-1-circle-brain-relay", capturedAuth);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://evil.intochat.test")]
    [InlineData("https://cloud.intochat.test:8443")]
    [InlineData("http://cloud.intochat.test")]
    public async Task LinkBinCredentialsAreNotAttachedToForeignOriginOnHttpOrWebSocket(string foreignBase)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var linkBinUri = new Uri("https://cloud.intochat.test");
        var foreignUri = new Uri(foreignBase);
        const string accessToken = "desktop-secret-must-not-leak";
        var (creds, _, tempDir) = await CreateLinkedCredentialsAsync(
            linkBinUri,
            accessToken,
            clock.GetUtcNow().AddHours(1),
            ct);

        try
        {
            var cloudClient = new CloudClient(new HttpClient(), creds, clock);
            var deviceProvider = new DeviceLinkCredentialsProvider(creds, cloudClient, clock);

            // 1. Direct DeviceLinkCredentialsProvider HTTP & stream origin checks fail closed.
            using var foreignReq = new HttpRequestMessage(HttpMethod.Get, new Uri(foreignUri, "/circle/v1/brains/next"));
            Assert.False(await deviceProvider.TryAuthorizeRequestAsync(foreignReq, ct));
            Assert.Null(foreignReq.Headers.Authorization);
            Assert.False(foreignReq.Headers.Contains(CircleRoutes.MachineProofHeader));

            var foreignStreamCreds = await deviceProvider.TryCreateStreamCredentialsAsync(
                new Uri(foreignUri, "/circle/v1/brains/stream"),
                forceRefresh: false,
                ct);
            Assert.Null(foreignStreamCreds);

            // 2. End-to-end KernelBrainRelayClientProvider with explicit foreign RelayBaseUri must not leak credentials.
            string? capturedHttpAuth = null;
            bool capturedHttpProof = false;
            var handler = new CapturingHttpHandler((req, _) =>
            {
                capturedHttpAuth = req.Headers.Authorization?.ToString();
                capturedHttpProof = req.Headers.Contains(CircleRoutes.MachineProofHeader);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IntoChatOS:BrainRelay:RelayBaseUri"] = foreignBase,
                    ["IntoChatOS:BrainRelay:AutoBindDeviceCredentials"] = "true",
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(creds);
            services.AddSingleton(cloudClient);
            services.AddSingleton<IDeviceLinkCredentialsProvider>(deviceProvider);
            services.AddKernelBrainRelayTransport();
            services.AddHttpClient(BrainRelayTransportHosting.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            var relayProvider = provider.GetRequiredService<KernelBrainRelayClientProvider>();
            var client = relayProvider.TryGetClient();
            Assert.NotNull(client);

            using var aliceKeys = BrainKeyPair.Generate();
            await using var os = DigitalBrain.Testing.TestOS.Local();
            var receiver = new BrainEnvelopeReceiver(
                new BrainId("alice"),
                aliceKeys,
                _ => null,
                os.Edge,
                os.InvokeLocalAsync,
                os.PostLocalAsync,
                clock: clock);

            // HTTP path: no Bearer or MachineProof attached.
            await client!.PollOnceAsync(receiver, ct);
            Assert.Null(capturedHttpAuth);
            Assert.False(capturedHttpProof);

            // WebSocket upgrade + auth-refresh paths: no Bearer or MachineProof attached.
            var refreshFrame = await relayProvider.CreateAuthRefreshFrameAsync(new Uri(foreignUri, "/circle/v1/brains/stream"), ct);
            Assert.Null(refreshFrame);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LinkBinCredentialsAreAttachedWhenOriginMatchesCaseInsensitivelyWithDefaultPort()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var linkBinUri = new Uri("https://cloud.intochat.test");
        const string accessToken = "desktop-matching-origin-token";
        var (creds, spki, tempDir) = await CreateLinkedCredentialsAsync(
            linkBinUri,
            accessToken,
            clock.GetUtcNow().AddHours(1),
            ct);

        try
        {
            var cloudClient = new CloudClient(new HttpClient(), creds, clock);
            var deviceProvider = new DeviceLinkCredentialsProvider(creds, cloudClient, clock);

            // Case-insensitive host + explicit default port 443 matches https://cloud.intochat.test.
            var matchingHttpUri = new Uri("https://CLOUD.INTOCHAT.TEST:443/circle/v1/brains/next");
            using var req = new HttpRequestMessage(HttpMethod.Get, matchingHttpUri);
            Assert.True(await deviceProvider.TryAuthorizeRequestAsync(req, ct));
            Assert.Equal($"Bearer {accessToken}", req.Headers.Authorization?.ToString());
            Assert.True(req.Headers.TryGetValues(CircleRoutes.MachineProofHeader, out var proofVals));
            var verify = MachineProof.Verify(
                spki,
                proofVals.Single(),
                "GET",
                matchingHttpUri,
                accessToken,
                clock.GetUtcNow(),
                new MemoryNonceCache());
            Assert.True(verify.IsValid, $"Expected valid machine proof, got {verify.Error}.");

            var matchingStreamUri = new Uri("https://CLOUD.INTOCHAT.TEST:443/circle/v1/brains/stream");
            var streamCreds = await deviceProvider.TryCreateStreamCredentialsAsync(matchingStreamUri, forceRefresh: false, ct);
            Assert.NotNull(streamCreds);
            Assert.Equal(accessToken, streamCreds!.AccessToken);
            Assert.False(string.IsNullOrWhiteSpace(streamCreds.MachineProof));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PollServiceDynamicallyDetectsNewlyWrittenLinkBinWithoutRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var dir = Path.Combine(Path.GetTempPath(), "intochat-relay-live-enroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "link.bin");
        var creds = new DeviceCredentials(new PassthroughKeyProtector(), path);
        var pollDetected = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var handler = new CapturingHttpHandler((req, _) =>
            {
                if (req.RequestUri is not null)
                {
                    pollDetected.TrySetResult(req.RequestUri);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });

            using var aliceKeys = BrainKeyPair.Generate();
            await using var os = DigitalBrain.Testing.TestOS.Local();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(creds);
            services.AddSingleton(new CloudClient(new HttpClient(handler), creds, clock));
            services.AddSingleton<IDeviceLinkCredentialsProvider>(sp =>
                new DeviceLinkCredentialsProvider(creds, sp.GetRequiredService<CloudClient>(), clock));
            services.AddSingleton(os.Edge);
            services.AddSingleton<ICallerAmbient>(os.Callers);
            services.AddSingleton<IBrainKeySigner>(new BrainKeyPairSigner(aliceKeys));
            services.AddKernelBrainRelayTransport();
            services.Configure<BrainRelayOptions>(opts =>
            {
                opts.PreferWebSocket = false;
                opts.EnrollmentWatchInterval = TimeSpan.FromSeconds(1);
            });
            services.AddHttpClient(BrainRelayTransportHosting.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            var relayProvider = provider.GetRequiredService<KernelBrainRelayClientProvider>();
            Assert.False(relayProvider.IsConfigured);

            var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<BrainRelayTransportHosting.BrainRelayPollService>()
                .Single();

            await hosted.StartAsync(ct);
            try
            {
                await Task.Delay(50, ct);
                Assert.False(pollDetected.Task.IsCompleted);

                // Write link.bin AFTER BrainRelayPollService started (no process restart!).
                using var machineKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                await creds.Write(new LinkSecrets(
                    Server: new Uri("https://live-enrolled.intochat.test"),
                    CredentialId: "cred-live-1",
                    AccessToken: "live-access-token",
                    AccessExpiresAt: clock.GetUtcNow().AddHours(1),
                    RefreshToken: "live-refresh-token",
                    RefreshExpiresAt: clock.GetUtcNow().AddDays(30),
                    PrivateKeyPkcs8: machineKey.ExportPkcs8PrivateKey()), ct);

                Assert.True(relayProvider.IsConfigured);

                // Advance clock past EnrollmentWatchInterval so PollService wakes up and begins polling.
                clock.Advance(TimeSpan.FromSeconds(2));
                var polledUri = await pollDetected.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.Equal("live-enrolled.intochat.test", polledUri.Host);
                Assert.Equal("/circle/v1/brains/next", polledUri.AbsolutePath);
            }
            finally
            {
                await hosted.StopAsync(ct);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
