using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Connections.Signals;
using IntoChat.OS.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

[Trait("host", "own")]
public sealed class ConnectionNeuronFacts
{
    [Fact]
    public async Task PublicCatalogSignalsFollowCommittedMembershipAndStatusWithoutCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var catalog = brain.Get<IConnectionRequests>(IConnectionRequests.Key(alice.BrainId));
        await using var changes = await brain.Observe<ConnectionsChanged>(catalog, ct);
        var connection = brain.Get<IConnection>(Key(alice));
        await connection.Connect("api-key", "expired");
        var connected = await changes.NextAsync(ct: ct);
        Assert.Equal("connected", Assert.Single(await catalog.ListConnections()).Status);
        await connection.Probe();
        var expired = await changes.NextAsync(ct: ct);
        Assert.True(expired.Revision > connected.Revision);
        Assert.Equal("expired", Assert.Single(await catalog.ListConnections()).Status);
        await connection.Disconnect();
        var disconnected = await changes.NextAsync(ct: ct);
        Assert.True(disconnected.Revision > expired.Revision);
        Assert.Empty(await catalog.ListConnections());
        Assert.DoesNotContain("expired", System.Text.Json.JsonSerializer.Serialize(connected), StringComparison.Ordinal);
    }

    private static string Key(Person person, string id = "one") => ConnectionKeys.For(person.BrainId, "example", id);

    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<ExampleConnectionModule>().StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task ConnectSealsTheValueAndTheSnapshotNeverShowsIt()
    {
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var connection = brain.Get<IConnection>(Key(alice));
        var view = await connection.Connect("api-key", "hunter2");
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Equal("api-key", view.AuthMethodId);
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(await connection.Read()), StringComparison.Ordinal);
        Assert.Equal("len:7", await brain.Get<IExample>(Key(alice)).Echo());
        Assert.Equal("hunter2", await brain.Get<IExample>(Key(alice)).SeenValue());
        // The connection is a plain neuron (06-08): its state is stored under its host grain.
        var storage = brain.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage);
        var state = new GrainState<ExampleState>();
        await storage.ReadStateAsync("example", Orleans.Runtime.GrainId.Create("neuron.host", ConnectionGrains.IdFor(Key(alice)).ToString()), state);
        Assert.True(state.RecordExists);
        var sealed_ = Assert.Single(state.State!.Secrets);
        Assert.Equal("value", sealed_.Name);
        Assert.DoesNotContain("hunter2", sealed_.Ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberOfAnotherBrainCannotConnectReadProbeDisconnectOrUseTheConnection()
    {
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        await brain.Get<IConnection>(Key(alice)).Connect("api-key", "hunter2");
        using var bob = await brain.PersonAsync("bob", TestContext.Current.CancellationToken);
        var connection = brain.Get<IConnection>(Key(alice));
        using (bob.AsCaller())
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => connection.Connect("api-key", "x"));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Read);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Probe);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Disconnect);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IExample>(Key(alice)).Echo());
        }
        Assert.Equal(ConnectionStatus.Connected, (await connection.Read()).Status);
    }

    [Fact]
    public async Task AnInstalledAppInTheOwningBrainCannotConnectProbeOrDisconnect()
    {
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var connection = brain.Get<IConnection>(Key(alice));
        await connection.Connect("api-key", "hunter2");
        using (alice.AsApp("app"))
        {
            var connect = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => connection.Connect("api-key", "x"));
            var probe = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Probe);
            var disconnect = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Disconnect);
            // The connection's own rule refused, not the call filter.
            Assert.All([connect, probe, disconnect], error => Assert.StartsWith("Only a member of the owning brain", error.Message, StringComparison.Ordinal));
        }
        Assert.Equal(ConnectionStatus.Connected, (await connection.Read()).Status);
    }

    [Fact]
    public async Task AProbeThatThrowsKeepsTheExceptionMessageOutOfTheDetailAndTheSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var connection = brain.Get<IConnection>(Key(alice));
        await using var failing = await brain.Observe<ConnectionFailing>(connection, ct);
        await connection.Connect("api-key", "throws");
        var view = await connection.Probe();
        Assert.Equal(ConnectionStatus.Failing, view.Status);
        Assert.Equal("The connector probe failed (HttpRequestException).", view.Detail);
        Assert.DoesNotContain(ExampleConnectionNeuron.LeakedToken, System.Text.Json.JsonSerializer.Serialize(await connection.Read()), StringComparison.Ordinal);
        var signal = await failing.NextAsync(ct: ct);
        Assert.Equal(view.Detail, signal.Detail);
        Assert.DoesNotContain(ExampleConnectionNeuron.LeakedToken, System.Text.Json.JsonSerializer.Serialize(signal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptGrantIsOnlyReachableWithTheOwnerStampOfTheKeysBrain()
    {
        await using var brain = await Start();
        var grant = new OAuthGrant("oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "scope", null, "{}");
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var bob = await brain.PersonAsync("bob", TestContext.Current.CancellationToken);
        using (bob.AsCaller())
        { await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Plain.GetConnectionInbound(Key(alice)).AcceptGrant(grant)); }
        using (alice.AsCaller())
        {
            var view = await brain.Plain.GetConnectionInbound(Key(alice)).AcceptGrant(grant);
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Equal(grant.ExpiresAt, view.ExpiresAt);
        }
    }

    [Fact]
    public async Task ProbeMovesStatusAndPublishesExactlyOneSignalPerTransition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var connection = brain.Get<IConnection>(Key(alice));
        await using var expired = await brain.Observe<ConnectionExpired>(connection, ct);
        await using var failing = await brain.Observe<ConnectionFailing>(connection, ct);
        await using var connected = await brain.Observe<ConnectionConnected>(connection, ct);
        await connection.Connect("api-key", "expired");
        Assert.Equal("one", (await connected.NextAsync(ct: ct)).ConnectionId);
        Assert.Equal(ConnectionStatus.Expired, (await connection.Probe()).Status);
        Assert.Equal("one", (await expired.NextAsync(ct: ct)).ConnectionId);
        await connection.Connect("api-key", "broken");
        await connected.NextAsync(ct: ct);
        Assert.Equal(ConnectionStatus.Failing, (await connection.Probe()).Status);
        Assert.Equal("broken by script", (await failing.NextAsync(ct: ct)).Detail);
        await connection.Connect("api-key", "fine");
        await connected.NextAsync(ct: ct);
        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        await connected.NextAsync(ct: ct);
        // Three connects and one healthy probe: one Connected each, nothing more.
        Assert.Equal(4, connected.Snapshot.Count);
        Assert.Single(expired.Snapshot);
        Assert.Single(failing.Snapshot);
    }

    [Fact]
    public async Task DisconnectClearsSecretsAndUntracksTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var connection = brain.Get<IConnection>(Key(alice));
        await connection.Connect("api-key", "hunter2");
        Assert.Equal([Key(alice)], await brain.Platform.Connections(alice.BrainId).List());
        await using var disconnected = await brain.Observe<ConnectionDisconnected>(connection, ct);
        await connection.Disconnect();
        await disconnected.NextAsync(ct: ct);
        Assert.Equal(ConnectionStatus.Disconnected, (await connection.Read()).Status);
        Assert.Empty(await brain.Platform.Connections(alice.BrainId).List());
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IExample>(Key(alice)).Echo());
    }

    [Fact]
    public async Task ConnectRefusesAnAuthMethodTheConnectorDoesNotDeclare()
    {
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IConnection>(Key(alice)).Connect("password", "x"));
    }

    [Fact]
    public async Task ConnectRefusesAnOAuthMethodBecauseOnlyTheCallbackGrantsOne()
    {
        await using var brain = await Start();
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var connection = brain.Get<IConnection>(Key(alice));
        await Assert.ThrowsAsync<ArgumentException>(() => connection.Connect("oauth", "pasted-token"));
        Assert.Equal(ConnectionStatus.Disconnected, (await connection.Read()).Status);
    }

    [Fact]
    public async Task ARefreshWithoutANewRefreshTokenKeepsTheSealedOneAndANewGrantWithoutOneClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var tokens = await TokenEndpointStub.StartAsync(ct);
        // Every refreshed token is already inside the 30-second lead, so each use refreshes again.
        tokens.RefreshExpiresIn = 1;
        await using var brain = await TestBrain.Create().WithModule<ExampleConnectionModule>()
            .WithIntegration(ExampleConnectionModule.Integration, "ClientId", "client-id")
            .WithIntegration(ExampleConnectionModule.Integration, "ClientSecret", "client-secret")
            .WithConfiguration(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ConnectionsTokenEndpointKey("example"), tokens.TokenEndpoint.AbsoluteUri)
            .StartAsync(ct);
        using var alice = await brain.PersonAsync("alice", TestContext.Current.CancellationToken);
        using var caller = alice.AsCaller();
        var inbound = brain.Plain.GetConnectionInbound(Key(alice));
        var example = brain.Get<IExample>(Key(alice));
        await inbound.AcceptGrant(new("oauth", "access-0", "refresh-0", DateTimeOffset.UtcNow.AddSeconds(-1), "read", null, "{}"));
        Assert.Equal("len:8", await example.Echo());
        Assert.Equal("len:8", await example.Echo());
        Assert.Equal(["refresh-0", "refresh-0"], tokens.RefreshTokens);
        Assert.Equal("access-2", await example.SeenValue());

        await inbound.AcceptGrant(new("oauth", "access-9", null, DateTimeOffset.UtcNow.AddSeconds(-1), "read", null, "{}"));
        await example.Echo();
        Assert.Equal(2, tokens.RefreshTokens.Length);
        Assert.Equal("access-9", await example.SeenValue());
    }
}
