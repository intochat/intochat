using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Connections;
using IntoChat.OS.Modules.Identity.Connections.OAuth;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;
using IntoChat.OS.Modules.Identity.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Identity.Tests.Unit;

public sealed class ConnectorCatalogAndDeviceFlowFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectorCatalogOffersCredentialAndPublicDeviceMethodsWhenConfidentialOAuthIsUnconfigured()
    {
        var connector = new ConnectorDefinition("forge", "Forge", ["web-oauth", "pat", "bearer-token", "device"], ["forge-oauth"])
        {
            OAuthMethods =
            [
                new OAuthMethod("web-oauth", "https://forge.example.test/oauth/authorize", "https://forge.example.test/oauth/token", ["repo"], "forge-oauth"),
            ],
            DeviceMethods =
            [
                new OAuthDeviceMethod("device", "https://forge.example.test/device/code", "https://forge.example.test/oauth/token", ["repo"], DefaultClientId: "forge-public-client", IntegrationId: "forge-oauth"),
            ],
        };
        var confidentialOnly = new ConnectorDefinition("crm", "CRM", ["org-oauth"], ["crm-oauth"])
        {
            OAuthMethods =
            [
                new OAuthMethod("org-oauth", "https://crm.example.test/oauth/authorize", "https://crm.example.test/oauth/token", ["api"], "crm-oauth"),
            ],
        };

        var forgeIntegration = new FakeIntegrationGrain("forge-oauth", IntegrationStatus.Unconfigured);
        var crmIntegration = new FakeIntegrationGrain("crm-oauth", IntegrationStatus.Unconfigured);
        var grains = new FakeGrainFactory(new Dictionary<string, IGrainWithStringKey>(StringComparer.Ordinal)
        {
            [IntegrationNeuron.KeyPrefix + "forge-oauth"] = forgeIntegration,
            [IntegrationNeuron.KeyPrefix + "crm-oauth"] = crmIntegration,
        });

        var services = new ServiceCollection();
        IReadOnlyList<ConnectorDefinition> definitions = [connector, confidentialOnly];
        services.AddSingleton(definitions);
        services.AddSingleton<IGrainFactory>(grains);
        services.AddSingleton(sp => new AuthorityNeurons(grains, sp));
        services.AddSingleton(sp => new ConnectorCatalog(definitions, sp.GetRequiredService<AuthorityNeurons>(), NullLogger<ConnectorCatalog>.Instance));
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<ConnectorCatalog>();
        var entries = await catalog.ListAsync(Ct);

        // Confidential-only connector stays hidden; hybrid connector is visible with non-confidential methods available.
        var entry = Assert.Single(entries);
        Assert.Equal("forge", entry.Id);
        Assert.Equal(["pat", "bearer-token", "device"], entry.AvailableAuthMethods);

        // Per-method availability never blocks pat, bearer-token, or public device flow with 409 Conflict.
        Assert.Null(await ConnectionLogin.UnavailableError(provider, "forge", "pat", Ct));
        Assert.Null(await ConnectionLogin.UnavailableError(provider, "forge", "bearer-token", Ct));
        Assert.Null(await ConnectionLogin.UnavailableError(provider, "forge", "device", Ct));

        // Confidential web OAuth remains unavailable until its integration is Ready.
        Assert.NotNull(await ConnectionLogin.UnavailableError(provider, "forge", "web-oauth", Ct));
        Assert.NotNull(await ConnectionLogin.UnavailableError(provider, "crm", "org-oauth", Ct));

        // Once the host integration becomes Ready, web-oauth becomes available dynamically.
        forgeIntegration.SetReady(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ClientId"] = "host-client-id",
            ["ClientSecret"] = "host-secret-placeholder",
        });

        var refreshed = await catalog.ListAsync(Ct);
        var updatedEntry = Assert.Single(refreshed);
        Assert.Equal(["web-oauth", "pat", "bearer-token", "device"], updatedEntry.AvailableAuthMethods);
        Assert.Null(await ConnectionLogin.UnavailableError(provider, "forge", "web-oauth", Ct));
    }

    [Fact]
    public async Task OAuthDeviceFlowSupportsFullRfc8628LifecycleZeroSecretSingleUseAndCrossBrainIsolation()
    {
        var connector = new ConnectorDefinition("forge", "Forge", ["device"], [])
        {
            OptionalIntegrations = ["forge-oauth"],
            DeviceMethods =
            [
                new OAuthDeviceMethod(
                    "device",
                    "https://forge.example.test/login/device/code",
                    "https://forge.example.test/login/oauth/access_token",
                    ["repo", "read:user"],
                    DefaultClientId: "default-public-client",
                    IntegrationId: "forge-oauth"),
            ],
        };

        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedDeviceFlowHandler();
        var inbound = new RecordingConnectionInbound();
        var integration = new FakeIntegrationGrain("forge-oauth", IntegrationStatus.Unconfigured);
        var flow = BuildDeviceFlow([connector], integration, fakeClock, handler, inbound);

        var brain = new BrainId("brain-alpha");
        var otherBrain = new BrainId("brain-beta");
        var key = ConnectionKeys.For(brain.Value, "forge", "main");

        // 1. Start device flow
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "device_code": "dc-sensitive-value-001",
              "user_code": "ABCD-1234",
              "verification_uri": "https://forge.example.test/login/device",
              "verification_uri_complete": "https://forge.example.test/login/device?user_code=ABCD-1234",
              "expires_in": 600,
              "interval": 5
            }
            """);

        var started = await flow.StartAsync(brain, key, "device", target: "org/repo", Ct);
        Assert.False(string.IsNullOrWhiteSpace(started.FlowId));
        Assert.Equal("ABCD-1234", started.UserCode);
        Assert.Equal("https://forge.example.test/login/device", started.VerificationUri);
        Assert.Equal("https://forge.example.test/login/device?user_code=ABCD-1234", started.VerificationUriComplete);
        Assert.Equal(600, started.ExpiresInSeconds);
        Assert.Equal(5, started.IntervalSeconds);

        // Verify start payload: client_id + scope, ZERO client_secret, and device_code is never returned.
        Assert.DoesNotContain("dc-sensitive-value-001", JsonSerializer.Serialize(started), StringComparison.Ordinal);
        var startForm = handler.CapturedForms[0];
        Assert.Equal("default-public-client", startForm["client_id"]);
        Assert.Equal("repo read:user", startForm["scope"]);
        Assert.False(startForm.ContainsKey("client_secret"));

        // 2. Cross-brain polling is rejected without consuming the flow.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.PollOnceAsync(otherBrain, started.FlowId, Ct));

        // 3. First poll: authorization_pending
        handler.EnqueueJson(HttpStatusCode.OK, """{"error":"authorization_pending"}""");
        var pending = await flow.PollOnceAsync(brain, started.FlowId, Ct);
        Assert.Equal(OAuthDevicePollStatus.Pending, pending.Status);
        Assert.Equal(5, pending.IntervalSeconds);
        Assert.Null(pending.Connection);

        var pollForm = handler.CapturedForms[1];
        Assert.Equal("default-public-client", pollForm["client_id"]);
        Assert.Equal("dc-sensitive-value-001", pollForm["device_code"]);
        Assert.Equal(OAuthDeviceFlow.DeviceCodeGrantType, pollForm["grant_type"]);
        Assert.False(pollForm.ContainsKey("client_secret"));

        // 4. Rapid poll before interval elapses returns Pending without calling provider again.
        var throttled = await flow.PollOnceAsync(brain, started.FlowId, Ct);
        Assert.Equal(OAuthDevicePollStatus.Pending, throttled.Status);
        Assert.Equal(2, handler.CapturedForms.Count);

        // 5. Advance clock and receive slow_down -> interval bumps by +5s (5 -> 10).
        fakeClock.Advance(TimeSpan.FromSeconds(5));
        handler.EnqueueJson(HttpStatusCode.BadRequest, """{"error":"slow_down"}""");
        var slowed = await flow.PollOnceAsync(brain, started.FlowId, Ct);
        Assert.Equal(OAuthDevicePollStatus.Pending, slowed.Status);
        Assert.Equal(10, slowed.IntervalSeconds);

        // 6. Advance clock by bumped interval (10s) and receive access_token -> sealed via AcceptGrant.
        fakeClock.Advance(TimeSpan.FromSeconds(10));
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "access_token": "sample-issued-bearer-999",
              "token_type": "bearer",
              "scope": "repo read:user",
              "expires_in": 3600
            }
            """);

        var connected = await flow.PollOnceAsync(brain, started.FlowId, Ct);
        Assert.Equal(OAuthDevicePollStatus.Connected, connected.Status);
        Assert.NotNull(connected.Connection);
        Assert.Equal(ConnectionStatus.Connected, connected.Connection!.Status);
        Assert.DoesNotContain("sample-issued-bearer-999", JsonSerializer.Serialize(connected), StringComparison.Ordinal);

        Assert.NotNull(inbound.LastGrant);
        Assert.Equal("device", inbound.LastGrant!.AuthMethodId);
        Assert.Equal("sample-issued-bearer-999", inbound.LastGrant.AccessToken);
        Assert.Equal("org/repo", inbound.LastGrant.Target);

        // 7. Single-use consumption: polling the completed flow again fails with KeyNotFoundException.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => flow.PollOnceAsync(brain, started.FlowId, Ct));
    }

    [Fact]
    public async Task OAuthDeviceFlowHandlesExpiredDeniedEphemeralBrainAndSsrfEndpoints()
    {
        var connector = new ConnectorDefinition("forge", "Forge", ["device"], [])
        {
            DeviceMethods =
            [
                new OAuthDeviceMethod(
                    "device",
                    "https://forge.example.test/login/device/code",
                    "https://forge.example.test/login/oauth/access_token",
                    ["repo"],
                    DefaultClientId: "default-public-client"),
            ],
        };

        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedDeviceFlowHandler();
        var inbound = new RecordingConnectionInbound();
        var brain = new BrainId("brain-alpha");
        var key = ConnectionKeys.For(brain.Value, "forge", "main");

        // access_denied removes flow
        var flow = BuildDeviceFlow([connector], null, fakeClock, handler, inbound);
        handler.EnqueueJson(HttpStatusCode.OK, """{"device_code":"dc-1","user_code":"DENY-1","verification_uri":"https://forge.example.test/device","expires_in":300,"interval":5}""");
        var deniedStart = await flow.StartAsync(brain, key, "device", ct: Ct);
        handler.EnqueueJson(HttpStatusCode.BadRequest, """{"error":"access_denied"}""");
        var denied = await flow.PollOnceAsync(brain, deniedStart.FlowId, Ct);
        Assert.Equal(OAuthDevicePollStatus.Denied, denied.Status);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => flow.PollOnceAsync(brain, deniedStart.FlowId, Ct));

        // expired_token removes flow
        handler.EnqueueJson(HttpStatusCode.OK, """{"device_code":"dc-2","user_code":"EXP-2","verification_uri":"https://forge.example.test/device","expires_in":300,"interval":5}""");
        var expiredStart = await flow.StartAsync(brain, key, "device", ct: Ct);
        handler.EnqueueJson(HttpStatusCode.BadRequest, """{"error":"expired_token"}""");
        var expired = await flow.PollOnceAsync(brain, expiredStart.FlowId, Ct);
        Assert.Equal(OAuthDevicePollStatus.Expired, expired.Status);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => flow.PollOnceAsync(brain, expiredStart.FlowId, Ct));

        // Ephemeral brain is blocked with ClaimRequiredException
        var ephemeralPolicy = new EphemeralDenyingPolicy();
        var ephemeralFlow = BuildDeviceFlow([connector], null, fakeClock, handler, inbound, policy: ephemeralPolicy);
        await Assert.ThrowsAsync<ClaimRequiredException>(() => ephemeralFlow.StartAsync(brain, key, "device", ct: Ct));

        // SSRF & non-HTTPS endpoint validation
        Assert.Throws<ArgumentException>(() => OAuthDeviceFlow.ValidateEndpoint("http://forge.example.test/device"));
        Assert.Throws<ArgumentException>(() => OAuthDeviceFlow.ValidateEndpoint("https://169.254.169.254/latest/meta-data"));
        Assert.Throws<ArgumentException>(() => OAuthDeviceFlow.ValidateEndpoint("https://10.0.0.1/device"));
        Assert.Throws<ArgumentException>(() => OAuthDeviceFlow.ValidateEndpoint("https://user:pass@forge.example.test/device"));
        Assert.Equal("https://forge.example.test/device", OAuthDeviceFlow.ValidateEndpoint("https://forge.example.test/device").AbsoluteUri);
        Assert.Equal("http://127.0.0.1:8080/device", OAuthDeviceFlow.ValidateEndpoint("http://127.0.0.1:8080/device").AbsoluteUri);
    }

    [Fact]
    public async Task ConnectionSecretBinderOpensVaultFormatsConnectsProbesAndFailsClosed()
    {
        var brain = new BrainId("brain-alpha");
        var key = ConnectionKeys.For(brain.Value, "forge", "main");
        var connection = new RecordingConnection("forge", "main");
        var vault = new FakeSecretVaultPort(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["secret://ada/forge-pat"] = "plain-secret-value-42",
        });

        var binder = BuildSecretBinder(key, connection, vault);

        // 1. BindAsync opens secret, applies formatValue, connects, and probes.
        var view = await binder.BindAsync(
            brain,
            "forge",
            "main",
            "pat",
            "secret://ada/forge-pat",
            formatValue: raw => $"formatted:{raw}",
            probe: true,
            ct: Ct);

        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Equal("pat", connection.LastAuthMethodId);
        Assert.Equal("formatted:plain-secret-value-42", connection.LastConnectedValue);
        Assert.Equal(1, connection.ProbeCount);

        // 2. BindGrantAsync with probe = false uses AcceptGrant and skips Probe.
        var grantView = await binder.BindGrantAsync(
            brain,
            "forge",
            key,
            "pat",
            "secret://ada/forge-pat",
            target: "owner/repo",
            scopes: "repo",
            probe: false,
            ct: Ct);

        Assert.Equal(ConnectionStatus.Connected, grantView.Status);
        Assert.NotNull(connection.LastGrant);
        Assert.Equal("plain-secret-value-42", connection.LastGrant!.AccessToken);
        Assert.Equal("owner/repo", connection.LastGrant.Target);
        Assert.Equal(1, connection.ProbeCount);

        // 3. Missing secret fails closed without calling Connect.
        var beforeCalls = connection.ConnectCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            binder.BindAsync(brain, "forge", "main", "pat", "secret://ada/missing", ct: Ct));
        Assert.Equal(beforeCalls, connection.ConnectCount);

        // 4. Ephemeral brain throws ClaimRequiredException.
        var ephemeralBinder = BuildSecretBinder(key, connection, vault, policy: new EphemeralDenyingPolicy());
        await Assert.ThrowsAsync<ClaimRequiredException>(() =>
            ephemeralBinder.BindAsync(brain, "forge", "main", "pat", "secret://ada/forge-pat", ct: Ct));
    }

    [Fact]
    public async Task HttpConnectionRoutesAllowPatWithoutOAuthServeDeviceFlowAndEnforce428ForEphemeralBrains()
    {
        var connector = new ConnectorDefinition("forge", "Forge", ["web-oauth", "pat", "device"], ["forge-oauth"])
        {
            OAuthMethods =
            [
                new OAuthMethod("web-oauth", "https://forge.example.test/oauth/authorize", "https://forge.example.test/oauth/token", ["repo"], "forge-oauth"),
            ],
            DeviceMethods =
            [
                new OAuthDeviceMethod("device", "https://forge.example.test/login/device/code", "https://forge.example.test/login/oauth/access_token", ["repo"], DefaultClientId: "forge-public-client", IntegrationId: "forge-oauth"),
            ],
        };

        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedDeviceFlowHandler();
        var connection = new RecordingConnection("forge", "main");
        var integration = new FakeIntegrationGrain("forge-oauth", IntegrationStatus.Unconfigured);
        var grains = new FakeGrainFactory(new Dictionary<string, IGrainWithStringKey>(StringComparer.Ordinal)
        {
            [IntegrationNeuron.KeyPrefix + "forge-oauth"] = integration,
        });
        var policy = new ToggleCapabilityPolicy();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(ConnectorCatalogAndDeviceFlowFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        IReadOnlyList<ConnectorDefinition> definitions = [connector];
        builder.Services.AddSingleton(definitions);
        builder.Services.AddSingleton<IGrainFactory>(grains);
        builder.Services.AddSingleton<IBrainAccess>(new AllowAllBrainAccess());
        builder.Services.AddSingleton<IBrainCapabilityPolicy>(policy);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Callers);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Authority);
        builder.Services.AddSingleton<TimeProvider>(fakeClock);
        builder.Services.AddSingleton(Options.Create(new ConnectionsOptions()));
        builder.Services.AddSingleton<IHttpClientFactory>(new SingleHandlerHttpClientFactory(handler));
        builder.Services.AddSingleton(sp => new AuthorityNeurons(grains, sp, _ => connection, _ => connection));
        builder.Services.AddSingleton(sp => new ConnectorCatalog(definitions, sp.GetRequiredService<AuthorityNeurons>(), NullLogger<ConnectorCatalog>.Instance));
        builder.Services.AddSingleton<OAuthDeviceRequests>();
        builder.Services.AddSingleton<IOAuthDeviceFlow>(sp => new OAuthDeviceFlow(
            definitions,
            sp.GetRequiredService<AuthorityNeurons>(),
            sp.GetRequiredService<OAuthDeviceRequests>(),
            sp.GetRequiredService<IOptions<ConnectionsOptions>>(),
            fakeClock,
            sp.GetRequiredService<IHttpClientFactory>(),
            IntoChat.OS.Tests.TestEdge.Authority,
            policy,
            NullLogger<OAuthDeviceFlow>.Instance));

        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            var brain = http.Request.Headers["X-Test-Brain"].FirstOrDefault() ?? "brain-alpha";
            using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("ada", "ada", new BrainId(brain))))
            {
                await next(http);
            }
        });
        app.MapConnectionNeurons();

        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            // 1. POST /connect with PAT succeeds (200 OK) even though forge-oauth is Unconfigured (no 409 Conflict!).
            var patResp = await client.PostAsJsonAsync(
                "/brains/brain-alpha/connections/main/connect",
                new { connectorId = "forge", authMethodId = "pat", value = "sample-pat-value" },
                Ct);
            Assert.Equal(HttpStatusCode.OK, patResp.StatusCode);

            // 2. POST /device/start succeeds and never leaks device_code.
            handler.EnqueueJson(HttpStatusCode.OK, """
                {
                  "device_code": "dc-http-secret-001",
                  "user_code": "HTTP-4242",
                  "verification_uri": "https://forge.example.test/login/device",
                  "expires_in": 600,
                  "interval": 5
                }
                """);
            var startResp = await client.PostAsJsonAsync(
                "/brains/brain-alpha/connections/main/device/start",
                new { connectorId = "forge", authMethodId = "device", target = "org/repo" },
                Ct);
            Assert.Equal(HttpStatusCode.OK, startResp.StatusCode);
            var startJson = await startResp.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("dc-http-secret-001", startJson, StringComparison.Ordinal);
            using var startDoc = JsonDocument.Parse(startJson);
            var flowId = startDoc.RootElement.GetProperty("flowId").GetString()!;
            Assert.Equal("HTTP-4242", startDoc.RootElement.GetProperty("userCode").GetString());

            // 3. Cross-brain /device/poll returns 403 Forbidden.
            using var crossReq = new HttpRequestMessage(HttpMethod.Post, "/brains/brain-beta/connections/main/device/poll")
            {
                Content = JsonContent.Create(new { flowId }),
            };
            crossReq.Headers.Add("X-Test-Brain", "brain-beta");
            var crossResp = await client.SendAsync(crossReq, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossResp.StatusCode);

            // 4. Owning brain /device/poll receives access_token and returns Connected without leaking token.
            handler.EnqueueJson(HttpStatusCode.OK, """{"access_token":"sample-http-access-888","scope":"repo"}""");
            var pollResp = await client.PostAsJsonAsync(
                "/brains/brain-alpha/connections/main/device/poll",
                new { flowId },
                Ct);
            Assert.Equal(HttpStatusCode.OK, pollResp.StatusCode);
            var pollJson = await pollResp.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("sample-http-access-888", pollJson, StringComparison.Ordinal);
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("Connected", pollDoc.RootElement.GetProperty("status").GetString());

            // 5. Ephemeral brain receives HTTP 428 Precondition Required on /device/start and /device/poll.
            policy.DenyAll = true;
            var ephemeralStart = await client.PostAsJsonAsync(
                "/brains/brain-alpha/connections/main/device/start",
                new { connectorId = "forge", authMethodId = "device" },
                Ct);
            Assert.Equal(HttpStatusCode.PreconditionRequired, ephemeralStart.StatusCode);

            var ephemeralPoll = await client.PostAsJsonAsync(
                "/brains/brain-alpha/connections/main/device/poll",
                new { flowId },
                Ct);
            Assert.Equal(HttpStatusCode.PreconditionRequired, ephemeralPoll.StatusCode);
        }
        finally
        {
            await app.StopAsync(Ct);
            await app.DisposeAsync();
        }
    }

    private static OAuthDeviceFlow BuildDeviceFlow(
        IReadOnlyList<ConnectorDefinition> connectors,
        FakeIntegrationGrain? integration,
        TimeProvider clock,
        HttpMessageHandler handler,
        RecordingConnectionInbound inbound,
        IBrainCapabilityPolicy? policy = null)
    {
        var grainsMap = new Dictionary<string, IGrainWithStringKey>(StringComparer.Ordinal);
        if (integration is not null)
        {
            grainsMap[IntegrationNeuron.KeyPrefix + integration.Id] = integration;
        }

        var grains = new FakeGrainFactory(grainsMap);
        var services = new ServiceCollection();
        services.AddSingleton(connectors);
        services.AddSingleton<IGrainFactory>(grains);
        var sp = services.BuildServiceProvider();
        var neurons = new AuthorityNeurons(grains, sp, connectionInboundOverride: _ => inbound);
        var options = Options.Create(new ConnectionsOptions());
        var requests = new OAuthDeviceRequests(options, clock);
        return new OAuthDeviceFlow(
            connectors,
            neurons,
            requests,
            options,
            clock,
            new SingleHandlerHttpClientFactory(handler),
            IntoChat.OS.Tests.TestEdge.Authority,
            policy,
            NullLogger<OAuthDeviceFlow>.Instance);
    }

    private static ConnectionSecretBinder BuildSecretBinder(
        string key,
        RecordingConnection connection,
        ISecretVaultPort vault,
        IBrainCapabilityPolicy? policy = null)
    {
        var grains = new FakeGrainFactory(new Dictionary<string, IGrainWithStringKey>(StringComparer.Ordinal));
        var sp = new ServiceCollection().BuildServiceProvider();
        var neurons = new AuthorityNeurons(
            grains,
            sp,
            connectionOverride: requested =>
            {
                Assert.Equal(key, requested);
                return connection;
            },
            connectionInboundOverride: requested =>
            {
                Assert.Equal(key, requested);
                return connection;
            });
        return new ConnectionSecretBinder(neurons, vault, policy, IntoChat.OS.Tests.TestEdge.Callers);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ScriptedDeviceFlowHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<Dictionary<string, string>> CapturedForms { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json) => _responses.Enqueue((status, json));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=', StringComparison.Ordinal);
                if (idx > 0)
                {
                    form[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
                }
            }
            CapturedForms.Add(form);

            var next = _responses.Dequeue();
            return new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class EphemeralDenyingPolicy : IBrainCapabilityPolicy
    {
        public CapabilityDecision Evaluate(BrainResidency residency, BrainCapability capability, string? resource = null, int turnsUsed = 0)
            => CapabilityDecision.RequireClaim(capability, "Sign in to claim your workspace.", resource);

        public ValueTask<CapabilityDecision> EvaluateAsync(string brainId, BrainCapability capability, string? resource = null, CancellationToken ct = default)
            => ValueTask.FromResult(CapabilityDecision.RequireClaim(capability, "Sign in to claim your workspace.", resource));
    }

    private sealed class ToggleCapabilityPolicy : IBrainCapabilityPolicy
    {
        public bool DenyAll { get; set; }

        public CapabilityDecision Evaluate(BrainResidency residency, BrainCapability capability, string? resource = null, int turnsUsed = 0)
            => DenyAll
                ? CapabilityDecision.RequireClaim(capability, "Sign in to claim your workspace.", resource)
                : CapabilityDecision.Allow(capability, resource);

        public ValueTask<CapabilityDecision> EvaluateAsync(string brainId, BrainCapability capability, string? resource = null, CancellationToken ct = default)
            => ValueTask.FromResult(DenyAll
                ? CapabilityDecision.RequireClaim(capability, "Sign in to claim your workspace.", resource)
                : CapabilityDecision.Allow(capability, resource));
    }

    private sealed class AllowAllBrainAccess : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string brainId, string principalId, string accountId, CancellationToken ct)
            => ValueTask.FromResult(true);
    }

    private sealed class FakeSecretVaultPort(Dictionary<string, string> secrets) : ISecretVaultPort
    {
        public Task<SecretVaultResult> Set(string name, string label, string value, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultResult.Denied);

        public Task<SecretVaultStatus> Remove(string name, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultStatus.Denied);

        public Task<SecretVaultResult> Resolve(SecretRef secret, CancellationToken cancellationToken)
            => Task.FromResult(secrets.TryGetValue(secret.Reference, out var value)
                ? SecretVaultResult.Resolved(value)
                : SecretVaultResult.NotFound);
    }

    private sealed class FakeIntegrationGrain(string id, IntegrationStatus status) : IIntegration
    {
        private IntegrationStatus _status = status;
        private long _revision = 1;
        private Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string Id => id;

        public void SetReady(Dictionary<string, string> values)
        {
            _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
            _status = IntegrationStatus.Ready;
            _revision++;
        }

        public Task<IntegrationSnapshot> Read()
            => Task.FromResult(new IntegrationSnapshot
            {
                IntegrationId = id,
                Status = _status,
                MissingFields = _status == IntegrationStatus.Ready ? [] : ["ClientId"],
                Revision = _revision,
            });

        public Task<IntegrationSnapshot> Configure(ConfigureIntegration input) => Read();
        public Task<IntegrationSnapshot> Seed(ConfigureIntegration input) => Read();
        public Task<IntegrationSnapshot> Clear(string field) => Read();
        public Task<ReleasedIntegration> Release() => Task.FromResult(new ReleasedIntegration { Values = _values });
    }

    private sealed class FakeGrainFactory(Dictionary<string, IGrainWithStringKey> grains) : IGrainFactory
    {
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey
            => (TGrainInterface)grains[primaryKey];

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainPrimaryKey) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    }

    private sealed class RecordingConnectionInbound : IConnectionInbound
    {
        public OAuthGrant? LastGrant { get; private set; }

        public Task<ConnectionView> AcceptGrant(OAuthGrant grant)
        {
            LastGrant = grant;
            return Task.FromResult(new ConnectionView("forge", "main", grant.AuthMethodId, ConnectionStatus.Connected, "octo", grant.ExpiresAt, null, "Connected"));
        }
    }

    private sealed class RecordingConnection(string connectorId, string connectionId) : IConnection, IConnectionInbound
    {
        public string? LastAuthMethodId { get; private set; }
        public string? LastConnectedValue { get; private set; }
        public OAuthGrant? LastGrant { get; private set; }
        public int ConnectCount { get; private set; }
        public int ProbeCount { get; private set; }

        public Task<ConnectionView> Read() => Task.FromResult(View());
        public Task<ConnectionView> Connect(string authMethodId, string value)
        {
            ConnectCount++;
            LastAuthMethodId = authMethodId;
            LastConnectedValue = value;
            return Task.FromResult(View());
        }

        public Task<ConnectionView> AcceptGrant(OAuthGrant grant)
        {
            LastGrant = grant;
            LastAuthMethodId = grant.AuthMethodId;
            return Task.FromResult(View());
        }

        public Task<ConnectionView> Probe()
        {
            ProbeCount++;
            return Task.FromResult(View());
        }

        public Task Disconnect() => Task.CompletedTask;
        public Task<ConnectionView> Adopt(string authMethodId, string? account) => Task.FromResult(View());
        public Task<Dictionary<string, SealedSecret>> ExportFor(string brainId) => Task.FromResult(new Dictionary<string, SealedSecret>());
        public Task<ConnectionView> Import(string authMethodId, string? account, Dictionary<string, SealedSecret> secrets, ConnectionStatus status, string? detail) => Task.FromResult(View());
        public Task ShareInto(string brainId, string app) => Task.CompletedTask;
        public Task<bool> IsSharedInto(string brainId, string app) => Task.FromResult(false);
        public Task UnshareFrom(string brainId, string app) => Task.CompletedTask;
        public Task ShareWith(string principalId) => Task.CompletedTask;
        public Task<bool> IsSharedWith(string principalId) => Task.FromResult(false);
        public Task UnshareWith(string principalId) => Task.CompletedTask;

        private ConnectionView View() => new(connectorId, connectionId, LastAuthMethodId ?? "pat", ConnectionStatus.Connected, "ada", null, null, "Connected");
    }
}
