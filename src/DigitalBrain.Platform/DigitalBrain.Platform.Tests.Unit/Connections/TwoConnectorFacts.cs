using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Module;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

// Two connectors in one silo: the OS composes several, so no route may resolve a connection by its
// contract alone.
public sealed class TwoConnectorFacts : IAsyncLifetime
{
    private TokenEndpointStub _stub = null!;
    private ModuleBrain _brain = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _stub = await TokenEndpointStub.StartAsync(ct);
        _brain = await ModuleTest.Create().WithModule<ExampleConnectionModule>().WithModule<SampleConnectionModule>()
            .WithExecution(new()
            {
                PrivateConfiguration = new Dictionary<string, string?>
                {
                    [IntegrationSchema.Key("example-oauth", "ClientId")] = "client-id",
                    [IntegrationSchema.Key("example-oauth", "ClientSecret")] = "client-secret",
                    [IntegrationSchema.Key("sample-oauth", "ClientId")] = "sample-client-id",
                    [IntegrationSchema.Key("sample-oauth", "ClientSecret")] = "sample-client-secret",
                    ["DigitalBrain:Connections:PublicOrigin"] = _stub.Origin.AbsoluteUri,
                    ["DigitalBrain:Connections:TokenEndpoints:example"] = _stub.TokenEndpoint.AbsoluteUri,
                    ["DigitalBrain:Connections:TokenEndpoints:sample"] = _stub.TokenEndpoint.AbsoluteUri,
                },
            })
            .WithHttpEdge().StartAsync(ct);
    }

    public async ValueTask DisposeAsync() { await _brain.DisposeAsync(); await _stub.DisposeAsync(); }

    private static string Key(string connector, string connection) => ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, connector, connection);

    [Fact]
    public async Task EachConnectorsConnectionsResolveThroughTheRoutesAndTheCallback()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var (connector, connection) in new[] { ("example", "pasted-example"), ("sample", "pasted-sample") })
        {
            using var connect = await _brain.HttpClient.PostAsJsonAsync($"/brains/owner/connections/{connection}/connect", new { connectorId = connector, authMethodId = "api-key", value = "hunter2" }, ct);
            Assert.True(connect.StatusCode == HttpStatusCode.OK, await connect.Content.ReadAsStringAsync(ct));
            Assert.Equal(connector, (await connect.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("connectorId").GetString());
        }
        foreach (var (connector, connection) in new[] { ("example", "login-example"), ("sample", "login-sample") })
        {
            using var login = await _brain.HttpClient.PostAsJsonAsync($"/brains/owner/connections/{connection}/login", new { connectorId = connector, authMethodId = "oauth" }, ct);
            login.EnsureSuccessStatusCode();
            var url = new Uri((await login.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
            var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
            using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/{connector}/callback?code=fake-code&state={state}", ct);
            Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        }

        var rows = (await _brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/connections", ct)).EnumerateArray()
            .ToDictionary(row => row.GetProperty("connectionId").GetString()!, row => (Connector: row.GetProperty("connectorId").GetString(), Status: row.GetProperty("status").GetString(), Method: row.GetProperty("authMethodId").GetString()));
        Assert.Equal(("example", "Connected", "api-key"), rows["pasted-example"]);
        Assert.Equal(("sample", "Connected", "api-key"), rows["pasted-sample"]);
        Assert.Equal(("example", "Connected", "oauth"), rows["login-example"]);
        Assert.Equal(("sample", "Connected", "oauth"), rows["login-sample"]);

        using var probe = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/pasted-sample/probe", new { }, ct);
        Assert.Equal("sample", (await probe.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("detail").GetString());

        CallerContextStamper.Stamp(new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        try
        {
            Assert.Equal("sample", (await _brain.Get<IConnection>(Key("sample", "login-sample")).Read()).ConnectorId);
            Assert.Equal("example", (await _brain.Get<IConnection>(Key("example", "login-example")).Read()).ConnectorId);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }

        foreach (var connection in new[] { "pasted-example", "pasted-sample", "login-example", "login-sample" })
        {
            using var disconnect = await _brain.HttpClient.PostAsJsonAsync($"/brains/owner/connections/{connection}/disconnect", new { }, ct);
            Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
        }
        Assert.Empty((await _brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/connections", ct)).EnumerateArray());
    }

    [Fact]
    public async Task AConnectionIdTakenByAnotherConnectorIsRefusedSoProbeAndDisconnectNameOneConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/connect", new { connectorId = "example", authMethodId = "api-key", value = "hunter2" }, ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var connect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/connect", new { connectorId = "sample", authMethodId = "api-key", value = "other" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, connect.StatusCode);
        using var login = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/login", new { connectorId = "sample", authMethodId = "oauth" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);
        using var again = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/connect", new { connectorId = "example", authMethodId = "api-key", value = "rotated" }, ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var probe = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/probe", new { }, ct);
        Assert.Equal("example", (await probe.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("connectorId").GetString());
        CallerContextStamper.Stamp(new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        try { Assert.Equal(ConnectionStatus.Disconnected, (await _brain.Get<IConnection>(Key("sample", "shared")).Read()).Status); }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }

        using var disconnect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/disconnect", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
        using var reuse = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/connect", new { connectorId = "sample", authMethodId = "api-key", value = "other" }, ct);
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
    }

    [Fact]
    public void AKeyWhoseConnectorIsNoConnectionNeuronIsRefusedBeforeAnyGrainIsAddressed()
    {
        foreach (var connector in new[] { "nosuch", "connection.index" })
        {
            var error = Assert.Throws<ArgumentException>(() => _brain.Get<IConnection>(Key(connector, "x")));
            Assert.Contains($"No connection neuron serves connector '{connector}'", error.Message, StringComparison.Ordinal);
        }
    }
}