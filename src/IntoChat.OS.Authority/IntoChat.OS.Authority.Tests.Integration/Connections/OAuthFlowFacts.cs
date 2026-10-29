using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Authority.Connections.OAuth;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Framework.Modules.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

[Trait("host", "own")]
public sealed class OAuthFlowFacts : IAsyncLifetime
{
    private TokenEndpointStub _stub = null!;
    private ITestBrain _brain = null!;
    private readonly List<(string? CallerBrain, string? TraceBrain)> _tokenContexts = [];

    public async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        _brain = await TestBrain.Create().WithModule<ExampleConnectionModule>()
            .WithIntegration(ExampleConnectionModule.Integration, "ClientId", "client-id")
            .WithIntegration(ExampleConnectionModule.Integration, "ClientSecret", "client-secret")
            .WithConfiguration("IntoChatOS:Connections:PublicOrigin", _stub.Origin.AbsoluteUri)
            .WithConfiguration("IntoChatOS:Connections:TokenEndpoints:example", _stub.TokenEndpoint.AbsoluteUri)
            .ConfigureServices(services => services.AddHttpClient("IntoChat.OS.Connections.OAuth")
                .AddHttpMessageHandler(() => new TokenContextCapture(_tokenContexts)))
            .StartAsync();
    }

    public async ValueTask DisposeAsync() { await _brain.DisposeAsync(); await _stub.DisposeAsync(); }

    private string Key => ConnectionKeys.For(_brain.Owner.BrainId, "example", "one");

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[]")]
    public async Task AnExplicitMalformedScopeCannotRestoreRequestedPermissions(string scope)
    {
        using var transport = new TokenTransport(new StringContent("{\"access_token\":\"access\",\"scope\":" + scope + "}"));
        using (_brain.Owner.AsCaller())
        {
            var error = await Assert.ThrowsAsync<OAuthTokenExchangeException>(() => Exchange(transport, TimeProvider.System)
                .RefreshAsync(Key, "oauth", "refresh", TestContext.Current.CancellationToken));
            Assert.Equal(OAuthTokenFailure.InvalidResponse, error.Failure);
        }
    }

    [Fact]
    public async Task ATokenResponseBodySharesTheWholeExchangeDeadline()
    {
        var clock = new FakeTimeProvider();
        using var blocked = new BlockingTokenStream();
        using var transport = new TokenTransport(new StreamContent(blocked));
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using (_brain.Owner.AsCaller())
        {
            var task = Exchange(transport, clock).RefreshAsync(Key, "oauth", "refresh", cleanup.Token);
            try
            {
                await blocked.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(30));
                var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                Assert.Same(task, completed);
                var error = await Assert.ThrowsAsync<OAuthTokenExchangeException>(() => task);
                Assert.Equal(OAuthTokenFailure.Unavailable, error.Failure);
            }
            finally
            {
                await cleanup.CancelAsync();
                try { await task; }
                catch (OperationCanceledException) { Assert.True(cleanup.IsCancellationRequested); }
                catch (OAuthTokenExchangeException error) { Assert.Equal(OAuthTokenFailure.Unavailable, error.Failure); }
            }
        }
    }

    [Fact]
    public async Task CallerCancellationWhileReadingATokenResponseIsPreserved()
    {
        using var blocked = new BlockingTokenStream();
        using var transport = new TokenTransport(new StreamContent(blocked));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using (_brain.Owner.AsCaller())
        {
            var task = Exchange(transport, TimeProvider.System).RefreshAsync(Key, "oauth", "refresh", cancellation.Token);
            await blocked.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    private OAuthTokenExchange Exchange(IHttpClientFactory transport, TimeProvider clock)
        => new([ExampleConnectionModule.Connector], _brain.Services.GetRequiredService<AuthorityNeurons>(),
            _brain.Services.GetRequiredService<IOptions<ConnectionsOptions>>(), clock, transport,
            _brain.Services.GetRequiredService<ModulePriceListRegistry>());

    [Fact]
    public async Task TokenExchangeHasTheStoredLoginOwnerAndTraceBeforeDispatch()
    {
        var url = await StartLogin();
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
        using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var captured = Assert.Single(_tokenContexts);
        Assert.Equal(_brain.Owner.BrainId, captured.CallerBrain);
        Assert.Equal(_brain.Owner.BrainId, captured.TraceBrain);
    }

    [Fact]
    public async Task RefreshSelectsTheConnectorFromTheFullConnectionKey()
    {
        using (_brain.Owner.AsCaller())
        {
            var grant = await _brain.Services.GetRequiredService<IOAuthTokenExchange>()
                .RefreshAsync(Key, "oauth", "refresh-token", TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(grant.AccessToken));
        }
        Assert.Equal("refresh_token", Assert.Single(_stub.Forms)["grant_type"]);
    }

    private async Task<Uri> StartLogin()
    {
        using var response = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/login", new { connectorId = "example", authMethodId = "oauth" });
        response.EnsureSuccessStatusCode();
        return new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!);
    }

    [Fact]
    public async Task AnOAuthLoginRoundTripsIntoAcceptGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = await StartLogin();
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("https://provider.test/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(new Uri(_stub.Origin, "connections/oauth/example/callback").AbsoluteUri, query["redirect_uri"]);
        using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={query["state"]}", ct);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var form = Assert.Single(_stub.Forms);
        Assert.Equal("fake-code", form["code"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.False(string.IsNullOrEmpty(form["code_verifier"]));
        using (_brain.Owner.AsCaller())
        {
            var view = await _brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Equal("oauth", view.AuthMethodId);
            Assert.NotNull(view.ExpiresAt);
        }
    }

    [Fact]
    public void TheHostNeverLogsACallbacksQueryWhateverItsLevel()
    {
        var filters = _brain.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoggerFilterOptions>>().Value;
        var rule = Assert.Single(filters.Rules, rule => rule.CategoryName == "Microsoft.AspNetCore.Hosting.Diagnostics" && rule.ProviderName is null);
        Assert.Equal(LogLevel.Warning, rule.LogLevel);
    }

    [Fact]
    public async Task ACallbackWithoutAClaimedRequestIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var unknown = await _brain.HttpClient.GetAsync("/connections/oauth/example/callback?code=x&state=not-a-request", ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var missing = await _brain.HttpClient.GetAsync("/connections/oauth/example/callback?code=x", ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task AStaleLoginRequestCannotBeReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = await StartLogin();
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
        using var first = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replay = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task ACallbackAfterARestartIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var url = await StartLogin();
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
        await _brain.RestartAsync(ct);
        using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        using (_brain.Owner.AsCaller()) { Assert.Equal(ConnectionStatus.Disconnected, (await _brain.Get<IConnection>(Key).Read()).Status); }
    }

    [Fact]
    public async Task ALoginStartRequiresADeclaredOAuthMethodAndAConfiguredIntegration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var wrong = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/login", new { connectorId = "example", authMethodId = "api-key" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        await _brain.Platform.Integration("example-oauth").Clear("ClientSecret");
        using var unconfigured = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/login", new { connectorId = "example", authMethodId = "oauth" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, unconfigured.StatusCode);
    }

    [Fact]
    public async Task ALoginStartRefusesAReadyIntegrationWithoutAClientSecretBeforeRedirecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _brain.Platform.Integration("example-oauth").Clear("ClientSecret");
        Assert.Equal(IntegrationStatus.Ready, (await _brain.Platform.Integration("example-oauth").Read()).Status);
        using var response = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/login", new { connectorId = "example", authMethodId = "oauth" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("Integration 'example-oauth' has no OAuth client configured.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client-id", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePastedValueRoutesConnectProbeAndDisconnectOneConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/two/connect", new { connectorId = "example", authMethodId = "api-key", value = "hunter2" }, ct);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
        var row = await connect.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("Connected", row.GetProperty("status").GetString());
        Assert.DoesNotContain("hunter2", await connect.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        var list = await _brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/connections", ct);
        Assert.Contains(list.EnumerateArray(), item => item.GetProperty("connectionId").GetString() == "two");
        using var probe = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/two/probe", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        using var disconnect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/two/disconnect", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
        list = await _brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/connections", ct);
        Assert.DoesNotContain(list.EnumerateArray(), item => item.GetProperty("connectionId").GetString() == "two");
    }

    [Fact]
    public async Task ThePastedValueRouteRefusesAnOAuthMethod()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/three/connect", new { connectorId = "example", authMethodId = "oauth", value = "pasted-token" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, connect.StatusCode);
        Assert.DoesNotContain("pasted-token", await connect.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    private sealed class TokenContextCapture(List<(string? CallerBrain, string? TraceBrain)> contexts) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? brain = null;
            try
            {
                var caller = CallerContextStamper.Require();
                brain = BrainScope.Create(caller.AccountId, caller.BrainId).Id;
            }
            catch (UntrustedCallerException) { }
            contexts.Add((brain, TraceContext.Current?.BrainId));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class TokenTransport(HttpContent content) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class BlockingTokenStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
