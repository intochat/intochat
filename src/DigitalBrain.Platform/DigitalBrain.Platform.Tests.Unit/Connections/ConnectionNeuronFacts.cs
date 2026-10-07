using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Connections.Signals;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

public sealed class ConnectionNeuronFacts
{
    private static readonly CallerContext Alice = new() { PrincipalId = "alice", AccountId = "alice", BrainId = "home", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };
    private static string Scope => BrainScope.Create("alice", "home").Id;
    private static string Key(string id = "one") => ConnectionKeys.For(Scope, "example", id);

    private static Task<ModuleBrain> Start() => ModuleTest.Create().WithModule<ExampleConnectionModule>().StartAsync(TestContext.Current.CancellationToken);

    private static void Stamp(CallerContext caller) => CallerContextStamper.Stamp(caller);
    private static void Clear() => Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);

    [Fact]
    public async Task ConnectSealsTheValueAndTheSnapshotNeverShowsIt()
    {
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var connection = brain.Get<IConnection>(Key());
            var view = await connection.Connect("api-key", "hunter2");
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Equal("api-key", view.AuthMethodId);
            Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(await connection.Read()), StringComparison.Ordinal);
            Assert.Equal("len:7", await brain.Get<IExample>(Key()).Echo());
            Assert.Equal("hunter2", await brain.Get<IExample>(Key()).SeenValue());
            var storage = brain.SiloServices.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.DefaultGrainStorage);
            var state = new GrainState<ExampleState>();
            await storage.ReadStateAsync("example", brain.Get<IExample>(Key()).GetGrainId(), state);
            Assert.True(state.RecordExists);
            var sealed_ = Assert.Single(state.State!.Secrets);
            Assert.Equal("value", sealed_.Name);
            Assert.DoesNotContain("hunter2", sealed_.Ciphertext, StringComparison.Ordinal);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AMemberOfAnotherBrainCannotConnectReadProbeDisconnectOrUseTheConnection()
    {
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            await brain.Get<IConnection>(Key()).Connect("api-key", "hunter2");
            Stamp(Alice with { BrainId = "other" });
            await brain.AuthorizeCallerAsync();
            var connection = brain.Get<IConnection>(Key());
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => connection.Connect("api-key", "x"));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Read);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Probe);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Disconnect);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IExample>(Key()).Echo());
            Stamp(Alice);
            Assert.Equal(ConnectionStatus.Connected, (await connection.Read()).Status);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AnInstalledAppInTheOwningBrainCannotConnectProbeOrDisconnect()
    {
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var connection = brain.Get<IConnection>(Key());
            await connection.Connect("api-key", "hunter2");
            Stamp(Alice with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy, AppId = "app" });
            var connect = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => connection.Connect("api-key", "x"));
            var probe = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Probe);
            var disconnect = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(connection.Disconnect);
            // The connection's own rule refused, not the call filter.
            Assert.All([connect, probe, disconnect], error => Assert.StartsWith("Only a member of the owning brain", error.Message, StringComparison.Ordinal));
            Stamp(Alice);
            Assert.Equal(ConnectionStatus.Connected, (await connection.Read()).Status);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task AProbeThatThrowsKeepsTheExceptionMessageOutOfTheDetailAndTheSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var connection = brain.Get<IConnection>(Key());
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
        finally { Clear(); }
    }

    [Fact]
    public async Task AcceptGrantIsOnlyReachableWithTheOwnerStampOfTheKeysBrain()
    {
        await using var brain = await Start();
        var grant = new OAuthGrant("oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "scope", null, "{}");
        Stamp(Alice with { BrainId = "other" });
        try
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Grains.GetConnectionInbound(Key()).AcceptGrant(grant));
            Stamp(Alice);
            var view = await brain.Grains.GetConnectionInbound(Key()).AcceptGrant(grant);
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Equal(grant.ExpiresAt, view.ExpiresAt);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task ProbeMovesStatusAndPublishesExactlyOneSignalPerTransition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var connection = brain.Get<IConnection>(Key());
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
        finally { Clear(); }
    }

    [Fact]
    public async Task DisconnectClearsSecretsAndUntracksTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var connection = brain.Get<IConnection>(Key());
            await connection.Connect("api-key", "hunter2");
            Assert.Equal([Key()], await brain.Grains.GetGrain<IConnectionIndex>(Scope).List());
            await using var disconnected = await brain.Observe<ConnectionDisconnected>(connection, ct);
            await connection.Disconnect();
            await disconnected.NextAsync(ct: ct);
            Assert.Equal(ConnectionStatus.Disconnected, (await connection.Read()).Status);
            Assert.Empty(await brain.Grains.GetGrain<IConnectionIndex>(Scope).List());
            await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IExample>(Key()).Echo());
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task InventoryListsOnlyTheAmbientBrainsConnectionsWithoutSecrets()
    {
        const string canary = "canary-connection-4b8e12";
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        try
        {
            foreach (var name in new[] { "one", "two" })
            {
                Stamp(Alice with { BrainId = name });
                await brain.AuthorizeCallerAsync();
                await brain.Grains.GetConnection(ConnectionKeys.For(BrainScope.CurrentId(), "example", name)).Connect("api-key", canary);
            }

            var provider = brain.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "connections");
            foreach (var name in new[] { "one", "two" })
            {
                Stamp(Alice with { BrainId = name });
                var scope = BrainScope.CurrentId();
                var key = ConnectionKeys.For(scope, "example", name);
                // Tracked but never activated, and tracked under a connector this silo does not compose.
                var dormant = ConnectionKeys.For(scope, "example", "dormant");
                var index = brain.Grains.GetGrain<IConnectionIndex>(scope);
                await index.Track(dormant);
                await index.Track(ConnectionKeys.For(scope, "nosuch", "gone"));

                var connected = new NeuronRecord("connections", "example", key, "example/" + name);
                var asleep = new NeuronRecord("connections", "example", dormant, "example/dormant");
                Assert.Equal([connected, asleep], await provider.Persisted(null, ct));
                Assert.Equal([connected], await provider.Active(null, ct));
                Assert.Empty(await provider.Persisted("sample", ct));
                Assert.DoesNotContain(canary, System.Text.Json.JsonSerializer.Serialize(await provider.Persisted(null, ct)), StringComparison.Ordinal);

                await brain.Grains.GetConnection(dormant).Read();
                Assert.Equal([connected, asleep], await provider.Active(null, ct));

                await brain.Grains.GetConnection(key).Disconnect();
                await brain.Grains.GetConnection(dormant).Disconnect();
                Assert.Empty(await provider.Persisted(null, ct));
            }
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task ConnectRefusesAnAuthMethodTheConnectorDoesNotDeclare()
    {
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try { await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IConnection>(Key()).Connect("password", "x")); }
        finally { Clear(); }
    }

    [Fact]
    public async Task ConnectRefusesAnOAuthMethodBecauseOnlyTheCallbackGrantsOne()
    {
        await using var brain = await Start();
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var connection = brain.Get<IConnection>(Key());
            await Assert.ThrowsAsync<ArgumentException>(() => connection.Connect("oauth", "pasted-token"));
            Assert.Equal(ConnectionStatus.Disconnected, (await connection.Read()).Status);
        }
        finally { Clear(); }
    }

    [Fact]
    public async Task ARefreshWithoutANewRefreshTokenKeepsTheSealedOneAndANewGrantWithoutOneClearsIt()
    {
        var exchange = new RecordingTokenExchange();
        await using var brain = await ModuleTest.Create().WithModule<ExampleConnectionModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IOAuthTokenExchange>(exchange))
            .StartAsync(TestContext.Current.CancellationToken);
        Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        try
        {
            var inbound = brain.Grains.GetConnectionInbound(Key());
            var example = brain.Get<IExample>(Key());
            await inbound.AcceptGrant(new("oauth", "access-0", "refresh-0", DateTimeOffset.UtcNow.AddSeconds(-1), "read", null, "{}"));
            Assert.Equal("len:8", await example.Echo());
            Assert.Equal("len:8", await example.Echo());
            Assert.Equal(["refresh-0", "refresh-0"], exchange.RefreshTokens);
            Assert.Equal("access-2", await example.SeenValue());

            await inbound.AcceptGrant(new("oauth", "access-9", null, DateTimeOffset.UtcNow.AddSeconds(-1), "read", null, "{}"));
            await example.Echo();
            Assert.Equal(2, exchange.RefreshTokens.Count);
            Assert.Equal("access-9", await example.SeenValue());
        }
        finally { Clear(); }
    }

    // Answers every refresh like Google: a new, already-lapsing access token and no refresh token.
    private sealed class RecordingTokenExchange : IOAuthTokenExchange
    {
        public List<string> RefreshTokens { get; } = [];

        public Task<OAuthGrant> ExchangeCodeAsync(string connectorId, string authMethodId, string code, string codeVerifier, string redirectUri, string? target, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OAuthGrant> RefreshAsync(string connectorId, string authMethodId, string refreshToken, CancellationToken ct)
        {
            lock (RefreshTokens)
            {
                RefreshTokens.Add(refreshToken);
                return Task.FromResult(new OAuthGrant(authMethodId, $"access-{RefreshTokens.Count}", null, DateTimeOffset.UtcNow.AddSeconds(-1), "read", null, "{}"));
            }
        }
    }
}
