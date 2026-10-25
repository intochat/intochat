using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

// The gallery's connections: which connectors this host offers and whether the brain has them connected.
// Connect itself is the user's own HTTP login (the shell's connection window); no script starts one.
[Trait("host", "own")]
public sealed class GalleryConnectFacts : IAsyncLifetime
{
    private TokenEndpointStub _stub = null!;
    private ITestBrain _brain = null!;

    public async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        _brain = await TestBrain.Create().WithModule<ExampleConnectionModule>()
            .WithIntegration(ExampleConnectionModule.Integration, "ClientId", "client-id")
            .WithIntegration(ExampleConnectionModule.Integration, "ClientSecret", "client-secret")
            .WithConfiguration(IntoChatOSConfiguration.ConnectionsPublicOriginKey, _stub.Origin.AbsoluteUri)
            .WithConfiguration(IntoChatOSConfiguration.ConnectionsTokenEndpointKey("example"), _stub.TokenEndpoint.AbsoluteUri)
            .StartAsync();
    }

    public async ValueTask DisposeAsync() { await _brain.DisposeAsync(); await _stub.DisposeAsync(); }

    private IConnectionRequests Requests => _brain.Get<IConnectionRequests>(_brain.Owner.BrainId);

    [Fact]
    public async Task AnOfferedConnectorReadsConnectedAfterTheUsersLoginCallback()
    {
        var ct = TestContext.Current.CancellationToken;
        using (_brain.Owner.AsCaller()) { Assert.Equal(new ConnectorOffer("example", "Example", true, false), Assert.Single(await Requests.ListOffers())); }
        using var start = await _brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/example/login", new { connectorId = "example", authMethodId = "oauth" }, ct);
        var url = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
        Assert.Equal("https", url.Scheme);
        using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={System.Web.HttpUtility.ParseQueryString(url.Query)["state"]}", ct);
        callback.EnsureSuccessStatusCode();
        using (_brain.Owner.AsCaller()) { Assert.True(Assert.Single(await Requests.ListOffers()).Connected); }
    }

    [Fact]
    public async Task AnUnconfiguredConnectorIsOfferedAsNotAvailable()
    {
        await _brain.Platform.Integration("example-oauth").Clear("ClientId");
        using (_brain.Owner.AsCaller()) { Assert.Equal(new ConnectorOffer("example", "Example", false, false), Assert.Single(await Requests.ListOffers())); }
    }

    [Fact]
    public async Task AnotherBrainsCallerIsRefusedByTheBrainGate()
    {
        var alice = await _brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using (alice.AsCaller()) { await Assert.ThrowsAsync<UnauthorizedAccessException>(Requests.ListOffers); }
    }
}
