using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

// The gallery's Connect: a script asks the brain's connection requests for the provider's sign-in
// page through the same login start the HTTP endpoint uses, and reads which connectors are offered.
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
    public async Task AConfiguredConnectorAnswersTheProvidersHttpsSignInPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using (_brain.Owner.AsCaller())
        {
            var login = await Requests.LoginUrl("example");
            Assert.Null(login.Message);
            var url = new Uri(login.Url!);
            Assert.Equal("https://provider.test/authorize", url.GetLeftPart(UriPartial.Path));
            Assert.Equal("client-id", System.Web.HttpUtility.ParseQueryString(url.Query)["client_id"]);
            Assert.Equal(new ConnectorOffer("example", "Example", true, false), Assert.Single(await Requests.ListOffers()));

            // The callback stores the connection: the offer now reads connected.
            var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
            using var callback = await _brain.HttpClient.GetAsync($"/connections/oauth/example/callback?code=fake-code&state={state}", ct);
            callback.EnsureSuccessStatusCode();
            Assert.True(Assert.Single(await Requests.ListOffers()).Connected);
            Assert.Equal(ConnectionStatus.Connected, (await _brain.Get<IConnection>(ConnectionKeys.For(_brain.Owner.BrainId, "example", "example")).Read()).Status);
        }
    }

    [Fact]
    public async Task AnUnconfiguredConnectorIsNotAvailableAndNoUrlIsMade()
    {
        await _brain.Platform.Integration("example-oauth").Clear("ClientId");
        using (_brain.Owner.AsCaller())
        {
            var login = await Requests.LoginUrl("example");
            Assert.Null(login.Url);
            Assert.Contains("not available on this host", login.Message, StringComparison.Ordinal);
            Assert.Equal(new ConnectorOffer("example", "Example", false, false), Assert.Single(await Requests.ListOffers()));
            Assert.Contains("not available on this host", (await Requests.LoginUrl("nope")).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnotherBrainsCallerIsRefusedByTheBrainGate()
    {
        var alice = await _brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using (alice.AsCaller()) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Requests.LoginUrl("example")); }
    }
}
