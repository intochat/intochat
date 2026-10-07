using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Connections.Signals;
using DigitalBrain.Salesforce;
using DigitalBrain.Salesforce.Signals;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Salesforce.Tests;

public sealed class SalesforceFacts
{
    private const string InstanceUrl = "https://acme.my.salesforce.com/";
    private static readonly string Key = ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "salesforce", "org");
    private static readonly CallerContext Owner = new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };

    [Fact]
    public async Task AGrantConnectsTheOrgAndRecordsItsInstanceUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await StartAsync(new FakeSalesforceProvider(), null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            await using var connected = await fixture.Brain.Observe<ConnectionConnected>(fixture.Brain.Get<ISalesforce>(Key), ct);

            await ConnectAsync(fixture.Brain);

            var signal = await connected.NextAsync(ct: ct);
            Assert.Equal("salesforce", signal.ConnectorId);
            Assert.Equal("org", signal.ConnectionId);
            Assert.Equal(InstanceUrl, signal.Account);
            var view = await fixture.Brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Equal("org-oauth", view.AuthMethodId);
            Assert.Equal(InstanceUrl, view.Account);
            Assert.DoesNotContain("access-token", JsonSerializer.Serialize(view), StringComparison.Ordinal);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AcceptGrantRejectsAGrantWithoutAnHttpsInstanceUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await StartAsync(new FakeSalesforceProvider(), null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var inbound = fixture.Brain.Grains.GetConnectionInbound(Key);
            await Assert.ThrowsAsync<SalesforceUnavailableException>(() => inbound.AcceptGrant(Grant("""{"access_token":"access-token"}""")));
            await Assert.ThrowsAsync<SalesforceUnavailableException>(() => inbound.AcceptGrant(Grant("""{"access_token":"access-token","instance_url":"http://acme.my.salesforce.com"}""")));

            Assert.Equal(ConnectionStatus.Disconnected, (await fixture.Brain.Get<IConnection>(Key).Read()).Status);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AWriteWithAnExpiredTokenRefreshesFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider();
        var exchange = new FakeTokenExchange();
        await using var fixture = await StartAsync(provider, exchange, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain, DateTimeOffset.UtcNow.AddSeconds(-1));

            await salesforce.PrepareWrite(new("createRecord", """{"Name":"Acme"}"""));

            Assert.Equal(["refresh-token"], exchange.RefreshTokens);
            Assert.Equal("fresh-access-token", provider.AccessToken);
            var view = await fixture.Brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.True(view.ExpiresAt > DateTimeOffset.UtcNow);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AGrantWithoutAnExpiryLivesTheDefaultSessionAndRefreshesOnceItLapses()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider { Result = JsonSerializer.SerializeToElement(new { totalSize = 0 }) };
        var exchange = new FakeTokenExchange();
        var clock = new ShiftedClock();
        await using var fixture = await StartAsync(provider, exchange, ct, clock);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var before = clock.GetUtcNow();
            var salesforce = await ConnectAsync(fixture.Brain, Grant(Org, expiresAt: null));
            var after = clock.GetUtcNow();
            var expiresAt = (await fixture.Brain.Get<IConnection>(Key).Read()).ExpiresAt;
            Assert.InRange(Assert.NotNull(expiresAt), before.AddMinutes(60), after.AddMinutes(60));

            await salesforce.Query(Soql, ct);
            Assert.Empty(exchange.RefreshTokens);
            Assert.Equal("access-token", provider.AccessToken);

            clock.Shift = TimeSpan.FromMinutes(61);
            await salesforce.Query(Soql, ct);

            Assert.Equal(["refresh-token"], exchange.RefreshTokens);
            Assert.Equal("fresh-access-token", provider.AccessToken);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task ARefreshWithoutAnExpiryAlsoLivesTheDefaultSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider { Result = JsonSerializer.SerializeToElement(new { totalSize = 0 }) };
        var exchange = new FakeTokenExchange { WithoutExpiry = true };
        var clock = new ShiftedClock();
        await using var fixture = await StartAsync(provider, exchange, ct, clock);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain, Grant(Org, clock.GetUtcNow().AddSeconds(-1)));

            var before = clock.GetUtcNow();
            await salesforce.Query(Soql, ct);
            var after = clock.GetUtcNow();

            Assert.Equal(["refresh-token"], exchange.RefreshTokens);
            var expiresAt = (await fixture.Brain.Get<IConnection>(Key).Read()).ExpiresAt;
            Assert.InRange(Assert.NotNull(expiresAt), before.AddMinutes(60), after.AddMinutes(60));
            await salesforce.Query(Soql, ct);
            Assert.Single(exchange.RefreshTokens);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AGrantForAnotherOrgDropsThePendingWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider { Result = JsonSerializer.SerializeToElement(new { id = "001xx000003DGbY" }) };
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);
            var preview = await salesforce.PrepareWrite(new("createRecord", """{"Name":"Acme"}"""));

            await ConnectAsync(fixture.Brain, Grant("""{"access_token":"access-token","instance_url":"https://other.my.salesforce.com"}"""));

            await Assert.ThrowsAsync<SalesforceUnavailableException>(() => salesforce.ConfirmWrite(new(preview.PreviewId, preview.ToolSchemaHash)));
            Assert.Equal("https://other.my.salesforce.com/", (await fixture.Brain.Get<IConnection>(Key).Read()).Account);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task ARefusedRefreshExpiresTheConnectionAndKeepsTheTokenOutOfTheDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider();
        var exchange = new FakeTokenExchange { Refuse = true };
        await using var fixture = await StartAsync(provider, exchange, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain, DateTimeOffset.UtcNow.AddSeconds(-1));
            await using var expired = await fixture.Brain.Observe<ConnectionExpired>(salesforce, ct);

            var refused = await Assert.ThrowsAsync<SalesforceNotConnectedException>(() => salesforce.Query(new("SELECT Id FROM Account WHERE Name != null LIMIT 10"), ct));

            Assert.Null(provider.AttemptedTool);
            Assert.DoesNotContain("refresh-token", refused.Message, StringComparison.Ordinal);
            await expired.NextAsync(ct: ct);
            var view = await fixture.Brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Expired, view.Status);
            Assert.DoesNotContain("refresh-token", view.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeTokenExchange.RefusalText, view.Detail, StringComparison.Ordinal);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AProbeAsksSalesforceWithTheStoredTokenAndMapsItsAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider();
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            await ConnectAsync(fixture.Brain);
            var connection = fixture.Brain.Get<IConnection>(Key);

            Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
            Assert.Equal("getUserInfo", provider.AttemptedTool);
            Assert.Equal("access-token", provider.AccessToken);

            provider.Failure = new SalesforceUnreachableException("Salesforce is unavailable. Check service access and try again later.");
            var failing = await connection.Probe();
            Assert.Equal(ConnectionStatus.Failing, failing.Status);
            Assert.DoesNotContain("access-token", failing.Detail, StringComparison.Ordinal);

            provider.Failure = null;
            Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AFailedSchemaReadMarksTheConnectionFailingAndTheNextWriteStillRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider { Failure = new SalesforceUnavailableException("The authenticated Salesforce catalog does not contain this tool.") };
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);

            await Assert.ThrowsAsync<SalesforceUnavailableException>(() => salesforce.PrepareWrite(new("createRecord", """{"Name":"Acme"}""")));
            Assert.Equal(ConnectionStatus.Failing, (await fixture.Brain.Get<IConnection>(Key).Read()).Status);

            provider.Failure = null;
            var preview = await salesforce.PrepareWrite(new("createRecord", """{"Name":"Acme"}"""));
            Assert.Equal("schema-hash", preview.ToolSchemaHash);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task ADisconnectClearsTheOrgAndPublishesConnectionDisconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider();
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);
            await using var disconnected = await fixture.Brain.Observe<ConnectionDisconnected>(salesforce, ct);

            await fixture.Brain.Get<IConnection>(Key).Disconnect();

            await disconnected.NextAsync(ct: ct);
            var view = await fixture.Brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Disconnected, view.Status);
            Assert.Null(view.Account);
            await Assert.ThrowsAsync<SalesforceNotConnectedException>(() => salesforce.ReadUserInfo(ct));
            Assert.Null(provider.AttemptedTool);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AMemberOfAnotherBrainCannotQueryOrWriteTheOrg()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider { Result = JsonSerializer.SerializeToElement(new { totalSize = 0 }) };
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);
            CallerContextStamper.Stamp(Owner with { BrainId = "other" });
            await fixture.Brain.AuthorizeCallerAsync();

            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => salesforce.Query(new("SELECT Id FROM Account WHERE Name != null LIMIT 10"), ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => salesforce.PrepareWrite(new("createRecord", """{"Name":"Acme"}""")));
            Assert.Null(provider.AttemptedTool);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task QueryResolvesTheAccessTokenFromTheSealedConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider
        {
            Result = JsonSerializer.SerializeToElement(new { totalSize = 2, records = Array.Empty<object>() }),
        };
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);

            await salesforce.Query(new("SELECT Id FROM Account WHERE Name != null LIMIT 10"), ct);

            Assert.Equal("access-token", provider.AccessToken);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task PrepareThenConfirmWritePublishesRecordWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider
        {
            SchemaHash = "schema-hash",
            Result = JsonSerializer.SerializeToElement(new { id = "001xx000003DGbY" }),
        };
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);
            await using var prepared = await fixture.Brain.Observe<SalesforceWritePrepared>(salesforce, ct);

            var preview = await salesforce.PrepareWrite(new("createRecord", """{"Name":"Acme"}"""));

            Assert.Equal("schema-hash", preview.ToolSchemaHash);
            Assert.Equal("createRecord", (await prepared.NextAsync(ct: ct)).Preview.Tool);

            await using var written = await fixture.Brain.Observe<RecordWritten>(salesforce, ct);
            var confirmed = await salesforce.ConfirmWrite(new(preview.PreviewId, preview.ToolSchemaHash));

            Assert.Equal("001xx000003DGbY", confirmed.RecordId);
            Assert.Equal("001xx000003DGbY", (await written.NextAsync(ct: ct)).Preview.RecordId);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task PrepareWriteRejectsAnUnadmittedTool()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await StartAsync(new FakeSalesforceProvider(), null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);

            await Assert.ThrowsAsync<SalesforceUnavailableException>(
                () => salesforce.PrepareWrite(new("deleteRecord", "{}")));
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task QueryRunsBoundedSoqlThroughTheProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeSalesforceProvider
        {
            Result = JsonSerializer.SerializeToElement(new { totalSize = 2, records = Array.Empty<object>() }),
        };
        await using var fixture = await StartAsync(provider, null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = await ConnectAsync(fixture.Brain);

            var result = await salesforce.Query(new("SELECT Id FROM Account WHERE Name != null LIMIT 10"), ct);

            Assert.Equal(2, result.TotalSize);
            Assert.Equal("soqlQuery", provider.AttemptedTool);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task QueryRejectsUnboundedSoql()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await StartAsync(new FakeSalesforceProvider(), null, ct);
        await AuthorizeOwnerAsync(fixture);
        try
        {
            var salesforce = fixture.Brain.Get<ISalesforce>(Key);

            await Assert.ThrowsAsync<SalesforceUnavailableException>(() => salesforce.Query(new("SELECT Id FROM Account"), ct));
        }
        finally { Clear(); }
    }

    private const string Org = """{"access_token":"access-token","refresh_token":"refresh-token","instance_url":"https://acme.my.salesforce.com"}""";
    private static readonly SoqlQuery Soql = new("SELECT Id FROM Account WHERE Name != null LIMIT 10");

    private static OAuthGrant Grant(string response) => Grant(response, DateTimeOffset.UtcNow.AddHours(1));

    private static OAuthGrant Grant(string response, DateTimeOffset? expiresAt)
        => new("org-oauth", "access-token", "refresh-token", expiresAt, "mcp_api refresh_token", null, response);

    // A stamp set inside an awaited helper does not flow back to the caller, so each fact stamps here first.
    private static Task AuthorizeOwnerAsync(Fixture fixture)
    {
        CallerContextStamper.Stamp(Owner);
        return fixture.Brain.AuthorizeCallerAsync();
    }

    private static void Clear() => Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);

    private static Task<ISalesforce> ConnectAsync(ModuleBrain brain, DateTimeOffset? expiresAt = null)
        => ConnectAsync(brain, Grant(Org, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)));

    private static async Task<ISalesforce> ConnectAsync(ModuleBrain brain, OAuthGrant grant)
    {
        await brain.Grains.GetConnectionInbound(Key).AcceptGrant(grant);
        return brain.Get<ISalesforce>(Key);
    }

    private static async Task<Fixture> StartAsync(FakeSalesforceProvider provider, FakeTokenExchange? exchange, CancellationToken cancellationToken, ShiftedClock? clock = null)
    {
        var brain = await ModuleTest.Create().WithModule<SalesforceModule>()
            .ConfigureSilo(silo =>
            {
                if (clock is not null)
                {
                    silo.Services.AddSingleton<TimeProvider>(clock);
                }
                silo.Services.AddSingleton<ISalesforceProvider>(provider);
                if (exchange is not null)
                {
                    silo.Services.AddSingleton<IOAuthTokenExchange>(exchange);
                }
            })
            .StartAsync(cancellationToken);
        return new Fixture(brain);
    }

    private sealed class Fixture(ModuleBrain brain) : IAsyncDisposable
    {
        internal ModuleBrain Brain { get; } = brain;

        public ValueTask DisposeAsync() => Brain.DisposeAsync();
    }
}

internal sealed class FakeSalesforceProvider : ISalesforceProvider
{
    public string SchemaHash { get; set; } = "schema-hash";

    public JsonElement Result { get; set; } = JsonSerializer.SerializeToElement(new { });

    public Exception? Failure { get; set; }

    public string? AttemptedTool { get; private set; }

    public string? AccessToken { get; private set; }

    public Task<JsonElement> InvokeAsync(string tool, JsonElement arguments, string accessToken, CancellationToken cancellationToken)
    {
        AttemptedTool = tool;
        AccessToken = accessToken;
        return Failure is { } failure ? Task.FromException<JsonElement>(failure) : Task.FromResult(Result);
    }

    public Task<string> ReadToolSchemaHashAsync(string tool, string accessToken, CancellationToken cancellationToken)
    {
        AttemptedTool = tool;
        AccessToken = accessToken;
        return Failure is { } failure ? Task.FromException<string>(failure) : Task.FromResult(SchemaHash);
    }
}

// Answers a refresh like Salesforce: a fresh access token, no new refresh token.
internal sealed class FakeTokenExchange : IOAuthTokenExchange
{
    public const string RefusalText = "invalid_grant for refresh-token";

    public bool Refuse { get; init; }

    // Salesforce's own refresh answer carries no expires_in.
    public bool WithoutExpiry { get; init; }

    public List<string> RefreshTokens { get; } = [];

    public Task<OAuthGrant> ExchangeCodeAsync(string connectorId, string authMethodId, string code, string codeVerifier, string redirectUri, string? target, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<OAuthGrant> RefreshAsync(string connectorId, string authMethodId, string refreshToken, CancellationToken ct)
    {
        lock (RefreshTokens) { RefreshTokens.Add(refreshToken); }
        // The message carries the token on purpose: the connection must keep it out of its detail.
        return Refuse
            ? Task.FromException<OAuthGrant>(new InvalidOperationException(RefusalText))
            : Task.FromResult(new OAuthGrant(authMethodId, "fresh-access-token", null, WithoutExpiry ? null : DateTimeOffset.UtcNow.AddHours(1), "mcp_api refresh_token", null,
                """{"access_token":"fresh-access-token","instance_url":"https://acme.my.salesforce.com"}"""));
    }
}

// The silo's wall clock moved forward by Shift; timers run on GetTimestamp, which it leaves alone.
internal sealed class ShiftedClock : TimeProvider
{
    public TimeSpan Shift { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Shift;
}
