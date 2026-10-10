using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

public sealed class OAuthFlowFacts : IAsyncLifetime
{
    private TokenEndpointStub _stub = null!;
    private ModuleBrain _brain = null!;

    public async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        _brain = await ModuleTest.Create().WithModule<ExampleConnectionModule>()
            .WithExecution(new()
            {
                PrivateConfiguration = new Dictionary<string, string?>
                {
                    [IntegrationSchema.Key("example-oauth", "ClientId")] = "client-id",
                    [IntegrationSchema.Key("example-oauth", "ClientSecret")] = "client-secret",
                    ["DigitalBrain:Connections:PublicOrigin"] = _stub.Origin.AbsoluteUri,
                    ["DigitalBrain:Connections:TokenEndpoints:example"] = _stub.TokenEndpoint.AbsoluteUri,
                },
            })
            .WithHttpEdge().StartAsync();
    }

    public async ValueTask DisposeAsync() { await _brain.DisposeAsync(); await _stub.DisposeAsync(); }

    private static string Key => ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "example", "one");

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
        Assert.Equal("fake-code", TokenEndpointStub.LastForm!["code"]);
        Assert.Equal("client-secret", TokenEndpointStub.LastForm["client_secret"]);
        Assert.False(string.IsNullOrEmpty(TokenEndpointStub.LastForm["code_verifier"]));
        CallerContextStamper.Stamp(new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        try
        {
            var view = await _brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Equal("oauth", view.AuthMethodId);
            Assert.NotNull(view.ExpiresAt);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public void TheHostNeverLogsACallbacksQueryWhateverItsLevel()
    {
        var filters = _brain.SiloServices.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<LoggerFilterOptions>>().Value;
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
        await _brain.RestartSiloAsync(ct);
        using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        CallerContextStamper.Stamp(new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        try { Assert.Equal(ConnectionStatus.Disconnected, (await _brain.Get<IConnection>(Key).Read()).Status); }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task ALoginStartRequiresADeclaredOAuthMethodAndAConfiguredIntegration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var wrong = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/login", new { connectorId = "example", authMethodId = "api-key" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        await _brain.Grains.GetGrain<IIntegration>("integration/example-oauth").Clear("ClientSecret");
        using var unconfigured = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/login", new { connectorId = "example", authMethodId = "oauth" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, unconfigured.StatusCode);
    }

    [Fact]
    public async Task ALoginStartRefusesAReadyIntegrationWithoutAClientSecretBeforeRedirecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await _brain.Grains.GetGrain<IIntegration>("integration/example-oauth").Clear("ClientSecret");
        Assert.Equal(IntegrationStatus.Ready, (await _brain.Grains.GetGrain<IIntegration>("integration/example-oauth").Read()).Status);
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
}
