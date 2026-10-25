using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Connections.Signals;
using IntoChat.OS.Modules.Salesforce.Signals;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Salesforce.Tests.Integration;

public sealed class SalesforceFacts(SalesforceHost host)
{
    private const string InstanceUrl = "https://acme.my.salesforce.com/";
    private static readonly SoqlQuery Soql = new("SELECT Id FROM Account WHERE Name != null LIMIT 10");
    private static readonly PrepareSalesforceWrite CreateAcme = new("createRecord", """{"Name":"Acme"}""");

    [Trait("host", "own")]
    [Fact]
    public async Task AGrantConnectsTheOrgAndRecordsItsInstanceUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        await using var connected = await org.Brain.Observe<ConnectionConnected>(org.Salesforce, ct);

        await org.ConnectAsync(ct);

        var signal = await connected.NextAsync(ct: ct);
        Assert.Equal("salesforce", signal.ConnectorId);
        Assert.Equal("org", signal.ConnectionId);
        Assert.Equal(InstanceUrl, signal.Account);
        using var caller = org.Owner.AsCaller();
        var view = await org.Connection.Read();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Equal("org-oauth", view.AuthMethodId);
        Assert.Equal(InstanceUrl, view.Account);
        Assert.DoesNotContain("access-token", JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AGrantWithoutAnHttpsInstanceUrlLeavesTheOrgDisconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);

        org.Fake.Grant = """{"access_token":"access-token"}""";
        Assert.NotEqual(HttpStatusCode.OK, await org.TryConnectAsync(ct));
        org.Fake.Grant = """{"access_token":"access-token","instance_url":"http://acme.my.salesforce.com"}""";
        Assert.NotEqual(HttpStatusCode.OK, await org.TryConnectAsync(ct));

        using var caller = org.Owner.AsCaller();
        Assert.Equal(ConnectionStatus.Disconnected, (await org.Connection.Read()).Status);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AWriteWithALapsedSessionRefreshesFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        await org.ConnectAsync(ct);
        org.Clock.Shift = TimeSpan.FromMinutes(61);
        using var caller = org.Owner.AsCaller();

        await org.Salesforce.PrepareWrite(CreateAcme);

        Assert.Equal(["refresh-token"], org.Fake.RefreshTokens);
        Assert.Equal("fresh-access-token", org.Fake.AccessToken);
        var view = await org.Connection.Read();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.True(view.ExpiresAt > org.Clock.GetUtcNow());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AGrantWithoutAnExpiryLivesTheDefaultSessionAndRefreshesOnceItLapses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        org.Fake.Result = JsonSerializer.SerializeToElement(new { totalSize = 0 });
        var before = org.Clock.GetUtcNow();
        await org.ConnectAsync(ct);
        var after = org.Clock.GetUtcNow();
        using var caller = org.Owner.AsCaller();
        Assert.InRange(Assert.NotNull((await org.Connection.Read()).ExpiresAt), before.AddMinutes(60), after.AddMinutes(60));

        await org.Salesforce.Query(Soql, ct);
        Assert.Empty(org.Fake.RefreshTokens);
        Assert.Equal("access-token", org.Fake.AccessToken);

        org.Clock.Shift = TimeSpan.FromMinutes(61);
        await org.Salesforce.Query(Soql, ct);

        Assert.Equal(["refresh-token"], org.Fake.RefreshTokens);
        Assert.Equal("fresh-access-token", org.Fake.AccessToken);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARefreshWithoutAnExpiryAlsoLivesTheDefaultSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        org.Fake.Result = JsonSerializer.SerializeToElement(new { totalSize = 0 });
        await org.ConnectAsync(ct);
        org.Clock.Shift = TimeSpan.FromMinutes(61);
        using var caller = org.Owner.AsCaller();

        var before = org.Clock.GetUtcNow();
        await org.Salesforce.Query(Soql, ct);
        var after = org.Clock.GetUtcNow();

        Assert.Equal(["refresh-token"], org.Fake.RefreshTokens);
        Assert.InRange(Assert.NotNull((await org.Connection.Read()).ExpiresAt), before.AddMinutes(60), after.AddMinutes(60));
        await org.Salesforce.Query(Soql, ct);
        Assert.Single(org.Fake.RefreshTokens);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AGrantForAnotherOrgDropsThePendingWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        org.Fake.Result = JsonSerializer.SerializeToElement(new { id = "001xx000003DGbY" });
        await org.ConnectAsync(ct);
        SalesforceWritePreview preview;
        using (org.Owner.AsCaller()) { preview = await org.Salesforce.PrepareWrite(CreateAcme); }

        org.Fake.Grant = """{"access_token":"access-token","instance_url":"https://other.my.salesforce.com"}""";
        await org.ConnectAsync(ct);

        using var caller = org.Owner.AsCaller();
        await Assert.ThrowsAsync<SalesforceUnavailableException>(() => org.Salesforce.ConfirmWrite(new(preview.PreviewId, preview.ToolSchemaHash)));
        Assert.Equal("https://other.my.salesforce.com/", (await org.Connection.Read()).Account);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARefusedRefreshExpiresTheConnectionAndKeepsTheTokenOutOfTheDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        org.Fake.RefuseRefresh = true;
        await org.ConnectAsync(ct);
        org.Clock.Shift = TimeSpan.FromMinutes(61);
        await using var expired = await org.Brain.Observe<ConnectionExpired>(org.Salesforce, ct);
        using var caller = org.Owner.AsCaller();

        var refused = await Assert.ThrowsAsync<SalesforceNotConnectedException>(() => org.Salesforce.Query(Soql, ct));

        Assert.DoesNotContain("soqlQuery", org.Fake.Requests);
        Assert.DoesNotContain("refresh-token", refused.Message, StringComparison.Ordinal);
        await expired.NextAsync(ct: ct);
        var view = await org.Connection.Read();
        Assert.Equal(ConnectionStatus.Expired, view.Status);
        Assert.DoesNotContain("refresh-token", view.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSalesforce.RefusalText, view.Detail, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProbeAsksSalesforceWithTheStoredTokenAndMapsItsAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        await org.ConnectAsync(ct);
        using var caller = org.Owner.AsCaller();

        Assert.Equal(ConnectionStatus.Connected, (await org.Connection.Probe()).Status);
        Assert.Contains("getUserInfo", org.Fake.Requests);
        Assert.Equal("access-token", org.Fake.AccessToken);

        org.Fake.Down = true;
        var failing = await org.Connection.Probe();
        Assert.Equal(ConnectionStatus.Failing, failing.Status);
        Assert.DoesNotContain("access-token", failing.Detail, StringComparison.Ordinal);

        org.Fake.Down = false;
        Assert.Equal(ConnectionStatus.Connected, (await org.Connection.Probe()).Status);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailedSchemaReadMarksTheConnectionFailingAndTheNextWriteStillRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        await org.ConnectAsync(ct);
        org.Fake.WithoutWriteTools = true;
        using var caller = org.Owner.AsCaller();

        await Assert.ThrowsAsync<SalesforceUnavailableException>(() => org.Salesforce.PrepareWrite(CreateAcme));
        Assert.Equal(ConnectionStatus.Failing, (await org.Connection.Read()).Status);

        org.Fake.WithoutWriteTools = false;
        var preview = await org.Salesforce.PrepareWrite(CreateAcme);
        Assert.NotEmpty(preview.ToolSchemaHash);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADisconnectClearsTheOrgAndPublishesConnectionDisconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        await org.ConnectAsync(ct);
        await using var disconnected = await org.Brain.Observe<ConnectionDisconnected>(org.Salesforce, ct);
        using var caller = org.Owner.AsCaller();

        await org.Connection.Disconnect();

        await disconnected.NextAsync(ct: ct);
        var view = await org.Connection.Read();
        Assert.Equal(ConnectionStatus.Disconnected, view.Status);
        Assert.Null(view.Account);
        await Assert.ThrowsAsync<SalesforceNotConnectedException>(() => org.Salesforce.ReadUserInfo(ct));
        Assert.Empty(org.Fake.Requests);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AMemberOfAnotherBrainCannotQueryOrWriteTheOrg()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        await org.ConnectAsync(ct);
        using var mallory = await org.Brain.PersonAsync("mallory", ct);
        using var caller = mallory.AsCaller();

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => org.Salesforce.Query(Soql, ct));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => org.Salesforce.PrepareWrite(CreateAcme));
        Assert.Empty(org.Fake.Requests);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task QueryRunsBoundedSoqlThroughSalesforceWithTheSealedAccessToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        org.Fake.Result = JsonSerializer.SerializeToElement(new { totalSize = 2, records = Array.Empty<object>() });
        await org.ConnectAsync(ct);
        using var caller = org.Owner.AsCaller();

        var result = await org.Salesforce.Query(Soql, ct);

        Assert.Equal(2, result.TotalSize);
        Assert.Equal(["soqlQuery"], org.Fake.Requests);
        Assert.Equal("access-token", org.Fake.AccessToken);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PrepareThenConfirmWritePublishesRecordWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.StartAsync(ct);
        org.Fake.Result = JsonSerializer.SerializeToElement(new { id = "001xx000003DGbY" });
        await org.ConnectAsync(ct);
        await using var prepared = await org.Brain.Observe<SalesforceWritePrepared>(org.Salesforce, ct);
        await using var written = await org.Brain.Observe<RecordWritten>(org.Salesforce, ct);
        using var caller = org.Owner.AsCaller();

        var preview = await org.Salesforce.PrepareWrite(CreateAcme);

        Assert.Equal("createRecord", (await prepared.NextAsync(ct: ct)).Preview.Tool);
        var confirmed = await org.Salesforce.ConfirmWrite(new(preview.PreviewId, preview.ToolSchemaHash));
        Assert.Equal("001xx000003DGbY", confirmed.RecordId);
        Assert.Equal("001xx000003DGbY", (await written.NextAsync(ct: ct)).Preview.RecordId);
    }

    [Fact]
    public async Task PrepareWriteRejectsAnUnadmittedTool()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.ForPersonAsync(host, ct);
        await org.ConnectAsync(ct);
        using var caller = org.Owner.AsCaller();

        await Assert.ThrowsAsync<SalesforceUnavailableException>(() => org.Salesforce.PrepareWrite(new("deleteRecord", "{}")));
    }

    [Fact]
    public async Task QueryRejectsUnboundedSoql()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var org = await Org.ForPersonAsync(host, ct);
        using var caller = org.Owner.AsCaller();

        await Assert.ThrowsAsync<SalesforceUnavailableException>(() => org.Salesforce.Query(new("SELECT Id FROM Account"), ct));
    }

    internal static TestBrain Compose(TestBrain brain, FakeSalesforce salesforce, ShiftedClock clock) => Org.Compose(brain, salesforce, clock);

    // One brain with the Salesforce module, its OAuth client and MCP endpoint pointed at the fake.
    private sealed class Org(ITestBrain brain, FakeSalesforce salesforce, ShiftedClock clock, Person? person = null) : IAsyncDisposable
    {
        public ITestBrain Brain => brain;
        public FakeSalesforce Fake => salesforce;
        public ShiftedClock Clock => clock;
        public Person Owner => person ?? brain.Owner;
        public string Key => ConnectionKeys.For(Owner.BrainId, "salesforce", "org");
        public ISalesforce Salesforce => brain.Get<ISalesforce>(Key);
        public IConnection Connection => brain.Get<IConnection>(Key);

        public static async Task<Org> StartAsync(CancellationToken ct)
        {
            var salesforce = await FakeSalesforce.StartAsync(ct);
            var clock = new ShiftedClock();
            return new Org(await Compose(TestBrain.Create(), salesforce, clock).StartAsync(ct), salesforce, clock);
        }

        // A fresh person on the shared host, whose fake and clock are the host's.
        public static async Task<Org> ForPersonAsync(SalesforceHost host, CancellationToken ct)
            => new(host.Brain, host.Fake, host.Clock, await host.PersonAsync(ct: ct));

        public static TestBrain Compose(TestBrain brain, FakeSalesforce salesforce, ShiftedClock clock)
        {
            var connections = IntoChatOSConfiguration.ConnectionsSectionName;
            return brain
                .WithModule<SalesforceModule, SalesforceModuleOptions>(options => { options.McpEndpoint = salesforce.McpEndpoint; options.AllowLoopbackMcp = true; })
                .WithIntegration(SalesforceModule.Integration, "ConsumerKey", "consumer-key")
                .WithIntegration(SalesforceModule.Integration, "ConsumerSecret", "consumer-secret")
                .WithConfiguration($"{connections}:PublicOrigin", salesforce.Origin.AbsoluteUri)
                .WithConfiguration($"{connections}:TokenEndpoints:salesforce", salesforce.TokenEndpoint.AbsoluteUri)
                // Session lifetimes are minutes long; the facts move the wall clock past them.
                .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
        }

        // The owner's browser login: the platform exchanges the code at the fake's token endpoint.
        public async Task<HttpStatusCode> TryConnectAsync(CancellationToken ct)
        {
            using var login = await Owner.Http.PostAsJsonAsync($"/brains/{Owner.BrainId}/connections/org/login", new { connectorId = "salesforce", authMethodId = "org-oauth" }, ct);
            login.EnsureSuccessStatusCode();
            var url = new Uri((await login.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
            var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
            using var callback = await Owner.Http.GetAsync($"/connections/oauth/salesforce/callback?code=fake-code&state={state}", ct);
            return callback.StatusCode;
        }

        public async Task ConnectAsync(CancellationToken ct) => Assert.Equal(HttpStatusCode.OK, await TryConnectAsync(ct));

        public async ValueTask DisposeAsync()
        {
            if (person is not null) { return; }
            await brain.DisposeAsync();
            await salesforce.DisposeAsync();
        }
    }
}
