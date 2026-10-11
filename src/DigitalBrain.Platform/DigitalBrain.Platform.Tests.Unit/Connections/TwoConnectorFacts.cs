using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

// Two connectors in one silo: the OS composes several, so no route may resolve a connection by its
// contract alone.
public sealed class TwoConnectorFacts : IAsyncLifetime
{
    private TokenEndpointStub _stub = null!;
    private ITestBrain _brain = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _stub = await TokenEndpointStub.StartAsync(ct);
        _brain = await TestBrain.Create().WithModule<ExampleConnectionModule>().WithModule<SampleConnectionModule>().WithModule<UnservedConnectorModule>()
            .WithIntegration(ExampleConnectionModule.Integration, "ClientId", "client-id")
            .WithIntegration(ExampleConnectionModule.Integration, "ClientSecret", "client-secret")
            .WithIntegration(SampleConnectionModule.Integration, "ClientId", "sample-client-id")
            .WithIntegration(SampleConnectionModule.Integration, "ClientSecret", "sample-client-secret")
            .WithConfiguration("DigitalBrain:Connections:PublicOrigin", _stub.Origin.AbsoluteUri)
            .WithConfiguration("DigitalBrain:Connections:TokenEndpoints:example", _stub.TokenEndpoint.AbsoluteUri)
            .WithConfiguration("DigitalBrain:Connections:TokenEndpoints:sample", _stub.TokenEndpoint.AbsoluteUri)
            .StartAsync(ct);
    }

    public async ValueTask DisposeAsync() { await _brain.DisposeAsync(); await _stub.DisposeAsync(); }

    private string Key(string connector, string connection) => ConnectionKeys.For(_brain.Owner.BrainId, connector, connection);

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

        using (_brain.Owner.AsCaller())
        {
            Assert.Equal("sample", (await _brain.Get<IConnection>(Key("sample", "login-sample")).Read()).ConnectorId);
            Assert.Equal("example", (await _brain.Get<IConnection>(Key("example", "login-example")).Read()).ConnectorId);
        }

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
        using (_brain.Owner.AsCaller()) { Assert.Equal(ConnectionStatus.Disconnected, (await _brain.Get<IConnection>(Key("sample", "shared")).Read()).Status); }

        using var disconnect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/disconnect", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
        using var reuse = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/shared/connect", new { connectorId = "sample", authMethodId = "api-key", value = "other" }, ct);
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
    }

    [Fact]
    public async Task ADeclaredConnectorWithoutAConnectionNeuronIsRefusedAtConnectAndLoginAndNotOfferedToTheShell()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/keys/connect", new { connectorId = "unserved", authMethodId = "api-key", value = "sk-pasted-secret" }, ct);
        var body = await connect.Content.ReadAsStringAsync(ct);
        Assert.True(connect.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains("No connection neuron serves connector 'unserved'", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-pasted-secret", body, StringComparison.Ordinal);

        using var login = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/keys/login", new { connectorId = "unserved", authMethodId = "oauth" }, ct);
        var loginBody = await login.Content.ReadAsStringAsync(ct);
        Assert.True(login.StatusCode == HttpStatusCode.BadRequest, loginBody);
        Assert.Contains("No connection neuron serves connector 'unserved'", loginBody, StringComparison.Ordinal);

        var offered = (await _brain.HttpClient.GetFromJsonAsync<JsonElement>("/connectors", ct)).EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).ToArray();
        Assert.Contains("example", offered);
        Assert.Contains("sample", offered);
        Assert.DoesNotContain("unserved", offered);
        Assert.Empty((await _brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/connections", ct)).EnumerateArray());
    }

    [Fact]
    public async Task TheConnectionListSkipsAKeyWhoseConnectorIsNoLongerComposed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _brain.Grains.GetGrain<IConnectionIndex>(_brain.Owner.BrainId).Track(Key("retired", "orphan"));
        using var connect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/kept/connect", new { connectorId = "example", authMethodId = "api-key", value = "hunter2" }, ct);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        using var list = await _brain.HttpClient.GetAsync("/brains/owner/connections", ct);
        Assert.True(list.StatusCode == HttpStatusCode.OK, await list.Content.ReadAsStringAsync(ct));
        var ids = (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Select(row => row.GetProperty("connectionId").GetString()).ToArray();
        Assert.Equal(["kept"], ids);
    }

    [Fact]
    public async Task ACallbackForAnIdAnotherConnectorTookDuringTheLoginIsRefusedAndTracksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var login = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/work/login", new { connectorId = "example", authMethodId = "oauth" }, ct);
        login.EnsureSuccessStatusCode();
        var state = System.Web.HttpUtility.ParseQueryString(new Uri((await login.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!).Query)["state"];
        using var connect = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/work/connect", new { connectorId = "sample", authMethodId = "api-key", value = "other" }, ct);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

        using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.DoesNotContain("fake-code", await callback.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        Assert.Equal([Key("sample", "work")], await _brain.Grains.GetGrain<IConnectionIndex>(_brain.Owner.BrainId).List());
        using (_brain.Owner.AsCaller()) { Assert.Equal(ConnectionStatus.Disconnected, (await _brain.Get<IConnection>(Key("example", "work")).Read()).Status); }
        using var probe = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/work/probe", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
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
