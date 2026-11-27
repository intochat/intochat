using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Connections.OAuth;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Tests.Unit.RuntimeHttp;

public sealed class OAuthConnectionsFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoginPageSetsPrivateHeadersAndHtmlEncodesContent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await LoginPage.WriteAsync(context, "Hello <script>", "Return to Acme & Co.", 400);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Contains("frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);

        context.Response.Body.Position = 0;
        var html = await new StreamReader(context.Response.Body).ReadToEndAsync(Ct);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("Hello &lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Return to Acme &amp; Co.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginFlowUsesParameterizedConfigHintWhenOriginMissingAndFallsBackToHttpRequest()
    {
        var options = Options.Create(new OAuthConnectionsOptions
        {
            ConfigHint = "Acme:Connections:PublicOrigin",
        });
        var carrier = new TestCallerCarrier();
        var edge = EdgeAuthority.CreateInternal(carrier);
        var exchange = new OAuthTokenExchange(
            [],
            new FakeIntegrationResolver([]),
            options,
            TimeProvider.System,
            new StubHttpClientFactory(new ScriptedHandler()),
            new ModulePriceListRegistry([]),
            edge);
        var flow = new OAuthLoginFlow(new OAuthLoginRequests(options, TimeProvider.System), exchange, options, carrier);

        var error = Assert.Throws<InvalidOperationException>(() => flow.CallbackUri("forge"));
        Assert.Contains("Acme:Connections:PublicOrigin", error.Message, StringComparison.Ordinal);

        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.acme.test", 8443);
        Assert.Equal("https://app.acme.test:8443/integrations/forge/oauth/callback", flow.CallbackUri("forge", http.Request).AbsoluteUri);
    }

    [Fact]
    public async Task LoginFlowGeneratesPkceChallengeAndEnforcesRequestCapacityAndExpiry()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new OAuthConnectionsOptions
        {
            PublicOrigin = new Uri("https://acme.test"),
            PendingLoginCapacity = 1,
            LoginLifetime = TimeSpan.FromMinutes(5),
        });
        var method = new OAuthMethod(
            "oauth",
            "https://provider.test/authorize",
            "https://provider.test/token",
            ["read:repo", "read:user"],
            "forge-oauth")
        {
            AuthorizationParameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["access_type"] = "offline",
            },
        };
        var connector = new ConnectorDefinition("forge", "Forge", ["oauth"], ["forge-oauth"]) { OAuthMethods = [method] };
        var integrations = new FakeIntegrationResolver(new Dictionary<string, FakeIntegrationHandle>(StringComparer.Ordinal)
        {
            ["forge-oauth"] = FakeIntegrationHandle.Ready("forge-oauth", "cid-123", "csec-456"),
        });
        var carrier = new TestCallerCarrier();
        var edge = EdgeAuthority.CreateInternal(carrier);
        var requests = new OAuthLoginRequests(options, clock);
        var exchange = new OAuthTokenExchange(
            [connector],
            integrations,
            options,
            clock,
            new StubHttpClientFactory(new ScriptedHandler()),
            new ModulePriceListRegistry([]),
            edge);
        var flow = new OAuthLoginFlow(requests, exchange, options, carrier);
        var key = ConnectionKeys.For("brain-1", "forge", "main");

        using (edge.Enter(edge.Person("ada", "acct-1", new BrainId("brain-1"))))
        {
            var url = await flow.StartAsync(key, "oauth", "org/repo", Ct);
            Assert.Equal("https://provider.test/authorize", url.GetLeftPart(UriPartial.Path));

            var query = ParseQuery(url.Query);
            Assert.Equal("code", query["response_type"]);
            Assert.Equal("cid-123", query["client_id"]);
            Assert.Equal("https://acme.test/integrations/forge/oauth/callback", query["redirect_uri"]);
            Assert.Equal("read:repo read:user", query["scope"]);
            Assert.Equal("offline", query["access_type"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.False(string.IsNullOrWhiteSpace(query["state"]));

            // Capacity of 1 rejects a second concurrent login until the first expires or is claimed.
            await Assert.ThrowsAsync<InvalidOperationException>(() => flow.StartAsync(key, "oauth", null, Ct));

            Assert.True(requests.TryClaim(query["state"], out var pending));
            Assert.Equal(key, pending.ConnectionKey);
            Assert.Equal("ada", pending.OwnerPrincipal);
            Assert.Equal("brain-1", pending.OwnerBrain);
            Assert.Equal("org/repo", pending.Target);

            var expectedChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pending.CodeVerifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(expectedChallenge, query["code_challenge"]);

            // Strictly single-use.
            Assert.False(requests.TryClaim(query["state"], out _));

            // Expired login cannot be claimed.
            var (expiredState, _) = requests.Begin(key, "oauth", null, "ada", "brain-1");
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.False(requests.TryClaim(expiredState, out _));
        }
    }

    [Fact]
    public async Task TokenExchangeUsesConfiguredHttpClientNameAndMapsProviderResponses()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new OAuthConnectionsOptions
        {
            HttpClientName = "Acme.Custom.OAuth",
            TokenEndpoints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["forge"] = "https://stub.provider.test/oauth/token",
            },
        });
        var method = new OAuthMethod("oauth", "https://provider.test/auth", "https://provider.test/token", ["repo"], "forge-oauth");
        var connector = new ConnectorDefinition("forge", "Forge", ["oauth"], ["forge-oauth"]) { OAuthMethods = [method] };
        var integrations = new FakeIntegrationResolver(new Dictionary<string, FakeIntegrationHandle>(StringComparer.Ordinal)
        {
            ["forge-oauth"] = FakeIntegrationHandle.Ready("forge-oauth", "cid-1", "csec-1"),
        });
        var handler = new ScriptedHandler();
        var clientFactory = new StubHttpClientFactory(handler);
        var carrier = new TestCallerCarrier();
        var edge = EdgeAuthority.CreateInternal(carrier);
        var exchange = new OAuthTokenExchange(
            [connector],
            integrations,
            options,
            clock,
            clientFactory,
            new ModulePriceListRegistry([]),
            edge);
        var key = ConnectionKeys.For("brain-1", "forge", "main");

        // Unstamped caller is refused.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            exchange.ExchangeCodeAsync(key, "oauth", "code-1", "verifier-1", "https://acme.test/cb", null, Ct));

        // Wrong brain is refused.
        using (edge.Enter(edge.Person("ada", "acct-1", new BrainId("brain-2"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                exchange.ExchangeCodeAsync(key, "oauth", "code-1", "verifier-1", "https://acme.test/cb", null, Ct));
        }

        using (edge.Enter(edge.Person("ada", "acct-1", new BrainId("brain-1"))))
        {
            // Successful code exchange.
            handler.Enqueue(HttpStatusCode.OK, """{"access_token":"tok-abc","refresh_token":"ref-xyz","expires_in":3600,"scope":"repo read:user"}""");
            var grant = await exchange.ExchangeCodeAsync(key, "oauth", "code-1", "verifier-1", "https://acme.test/cb", "org/repo", Ct);
            Assert.Equal("Acme.Custom.OAuth", clientFactory.RequestedNames.Single());
            Assert.Equal("https://stub.provider.test/oauth/token", handler.RequestedUris.Single().AbsoluteUri);
            Assert.Equal("tok-abc", grant.AccessToken);
            Assert.Equal("ref-xyz", grant.RefreshToken);
            Assert.Equal(clock.GetUtcNow().AddSeconds(3600), grant.ExpiresAt);
            Assert.Equal("repo read:user", grant.Scopes);
            Assert.Equal("org/repo", grant.Target);

            // invalid_grant maps to ReconsentRequired.
            handler.Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            var reconsent = await Assert.ThrowsAsync<OAuthTokenExchangeException>(() =>
                exchange.RefreshAsync(key, "oauth", "ref-xyz", Ct));
            Assert.Equal(OAuthTokenFailure.ReconsentRequired, reconsent.Failure);

            // Control characters in access_token map to InvalidResponse.
            handler.Enqueue(HttpStatusCode.OK, """{"access_token":"bad\ntoken"}""");
            var invalid = await Assert.ThrowsAsync<OAuthTokenExchangeException>(() =>
                exchange.RefreshAsync(key, "oauth", "ref-xyz", Ct));
            Assert.Equal(OAuthTokenFailure.InvalidResponse, invalid.Failure);
        }
    }

    [Fact]
    public async Task CallbackRendersParameterizedProductNameRedactsActivityAndDeliversGrantViaNeuronProxies()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var method = new OAuthMethod("oauth", "https://provider.test/auth", "https://provider.test/token", ["repo"], "forge-oauth");
        var connector = new ConnectorDefinition("forge", "Forge", ["oauth"], ["forge-oauth"]) { OAuthMethods = [method] };
        var integrations = new FakeIntegrationResolver(new Dictionary<string, FakeIntegrationHandle>(StringComparer.Ordinal)
        {
            ["forge-oauth"] = FakeIntegrationHandle.Ready("forge-oauth", "cid-1", "csec-1"),
        });
        var handler = new ScriptedHandler();
        var carrier = new TestCallerCarrier();
        var edge = EdgeAuthority.CreateInternal(carrier);
        var inbound = new RecordingConnectionInbound();
        var directory = new FakeDirectoryPort(new Member
        {
            BrainId = "brain-1",
            AccountId = "acct-1",
            PrincipalId = "ada",
            DisplayName = "Ada",
            Role = MemberRole.Owner,
        });

        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<ConnectorDefinition>>([connector]);
        services.AddSingleton<IOAuthIntegrationResolver>(integrations);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        services.AddSingleton(new ModulePriceListRegistry([]));
        services.AddSingleton(edge);
        services.AddSingleton<ICallerAmbient>(carrier);
        services.AddSingleton<IIdentityDirectoryPort>(directory);
        services.AddSingleton<INeuronProxies>(new SingleInboundProxies(inbound));
        services.AddSingleton<ILogger<OAuthCallback>>(NullLogger<OAuthCallback>.Instance);
        services.AddDigitalBrainOAuthConnections(o =>
        {
            o.PublicOrigin = new Uri("https://acme.test");
            o.ProductName = "Acme Workspace";
        });

        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        foreach (var surface in provider.GetServices<IHttpSurface>())
        {
            surface.Map(app);
        }
        var pipeline = app.Build();

        // 1. Expired/unknown state renders configured ProductName (never a hardcoded product string).
        var expiredContext = CreateCallbackContext(provider, "/integrations/forge/oauth/callback", "?code=secret-code&state=missing");
        using (var activity = new Activity("oauth.test").Start())
        {
            activity.SetTag("url.query", "?code=secret-code&state=missing");
            await pipeline(expiredContext);
            Assert.Null(activity.GetTagItem("url.query"));
            Assert.Equal("/integrations/forge/oauth/callback", activity.GetTagItem("http.target"));
            Assert.Equal("https://acme.test/integrations/forge/oauth/callback", activity.GetTagItem("url.full"));
        }
        Assert.Equal(400, expiredContext.Response.StatusCode);
        var expiredHtml = await ReadResponseBodyAsync(expiredContext);
        Assert.Contains("Return to Acme Workspace and connect again.", expiredHtml, StringComparison.Ordinal);

        // 2. Valid callback exchanges code, delivers grant to IConnectionInbound via INeuronProxies, and renders ProductName.
        var key = ConnectionKeys.For("brain-1", "forge", "main");
        var flow = provider.GetRequiredService<OAuthLoginFlow>();
        string state;
        using (edge.Enter(edge.Person("ada", "acct-1", new BrainId("brain-1"))))
        {
            var startUri = await flow.StartAsync(key, "oauth", "org/repo", Ct);
            state = ParseQuery(startUri.Query)["state"];
        }

        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"tok-live","scope":"repo"}""");
        var okContext = CreateCallbackContext(provider, "/integrations/forge/oauth/callback", $"?code=auth-code-99&state={state}");
        await pipeline(okContext);

        Assert.Equal(200, okContext.Response.StatusCode);
        var okHtml = await ReadResponseBodyAsync(okContext);
        Assert.Contains("Acme Workspace is continuing your request.", okHtml, StringComparison.Ordinal);
        Assert.NotNull(inbound.LastGrant);
        Assert.Equal("tok-live", inbound.LastGrant!.AccessToken);
        Assert.Equal("org/repo", inbound.LastGrant.Target);

        // 3. Non-member owner fails closed with 400 and does not deliver a grant.
        directory.MemberToReturn = null;
        string state2;
        using (edge.Enter(edge.Person("ada", "acct-1", new BrainId("brain-1"))))
        {
            var startUri = await flow.StartAsync(key, "oauth", null, Ct);
            state2 = ParseQuery(startUri.Query)["state"];
        }
        inbound.LastGrant = null;
        var deniedContext = CreateCallbackContext(provider, "/integrations/forge/oauth/callback", $"?code=auth-code-100&state={state2}");
        await pipeline(deniedContext);
        Assert.Equal(400, deniedContext.Response.StatusCode);
        Assert.Null(inbound.LastGrant);
        var deniedHtml = await ReadResponseBodyAsync(deniedContext);
        Assert.Contains("Return to Acme Workspace and connect again.", deniedHtml, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateCallbackContext(IServiceProvider provider, string path, string query)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("acme.test");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync(Ct);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=', StringComparison.Ordinal);
            if (idx > 0)
            {
                result[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
            }
        }
        return result;
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class TestCallerCarrier : ICallerCarrier
    {
        private readonly AsyncLocal<Caller?> _caller = new();
        private readonly AsyncLocal<bool> _kernel = new();

        public Caller? Current => _caller.Value;
        public bool IsKernelActive => _kernel.Value || _caller.Value?.Actor is Actor.Kernel;

        public IDisposable Enter(Caller? caller)
        {
            var prev = _caller.Value;
            _caller.Value = caller;
            return new Scope(() => _caller.Value = prev);
        }

        public IDisposable EnterKernelScope(bool clearCaller)
        {
            var prevKernel = _kernel.Value;
            var prevCaller = _caller.Value;
            _kernel.Value = true;
            if (clearCaller) { _caller.Value = null; }
            return new Scope(() => { _kernel.Value = prevKernel; _caller.Value = prevCaller; });
        }

        private sealed class Scope(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private sealed class FakeIntegrationResolver(Dictionary<string, FakeIntegrationHandle> handles) : IOAuthIntegrationResolver
    {
        public IIntegrationHandle Get(string integrationId) => handles[integrationId];
    }

    private sealed class FakeIntegrationHandle(string id, IntegrationStatus status, Dictionary<string, string> values) : IIntegrationHandle
    {
        public static FakeIntegrationHandle Ready(string id, string clientId, string clientSecret)
            => new(id, IntegrationStatus.Ready, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ClientId"] = clientId,
                ["ClientSecret"] = clientSecret,
            });

        public string IntegrationId => id;
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = id, Status = status, MissingFields = [], Revision = 1 });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => Task.FromResult(values[field]);
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ReleasedIntegration { Values = values });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];
        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<Uri> RequestedUris { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingConnectionInbound : IConnectionInbound
    {
        public OAuthGrant? LastGrant { get; set; }

        public Task<ConnectionView> AcceptGrant(OAuthGrant grant)
        {
            LastGrant = grant;
            return Task.FromResult(new ConnectionView("forge", "main", grant.AuthMethodId, ConnectionStatus.Connected, "ada", grant.ExpiresAt, null, "Connected"));
        }
    }

    private sealed class SingleInboundProxies(IConnectionInbound inbound) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => (T)(object)inbound;
    }

    private sealed class FakeDirectoryPort(Member? member) : IIdentityDirectoryPort
    {
        public Member? MemberToReturn { get; set; } = member;

        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
            => Task.FromResult(MemberToReturn);

        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnsureAccount(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
