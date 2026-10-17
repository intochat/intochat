using DigitalBrain.ClickHouse;
using DigitalBrain.ClickHouse.Query;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Testing;

namespace DigitalBrain.Modules.ClickHouse.Tests;

public sealed class ClickHouseFacts
{
    private const string Sql = "SELECT count() AS value FROM companies";

    [Fact]
    public async Task TheDefaultNeuronQueriesTheDeploymentDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeClickHouseServer.StartAsync(ct);
        await using var brain = await StartAsync(server, ct);

        var result = await brain.Get<IClickHouse>(ClickHouseNames.DefaultNeuron).Query(new(Sql), ct);

        Assert.Equal("value", Assert.Single(result.Columns).Name);
        Assert.Equal("42", Assert.Single(Assert.Single(result.Rows)));
        Assert.Equal("deployment", server.Databases.Last());
    }

    [Fact]
    public async Task TheDefaultNeuronReportsTheServerItReaches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeClickHouseServer.StartAsync(ct);
        await using var brain = await StartAsync(server, ct);

        var connection = await brain.Get<IClickHouse>(ClickHouseNames.DefaultNeuron).ReadConnection(ct);

        Assert.True(connection.Connected);
        Assert.Equal("deployment", connection.Database);
        Assert.Equal("24.1", connection.ServerVersion);
        Assert.Equal(ClickHouseModule.DriverProviderName, connection.Provider);
    }

    [Fact]
    public async Task QueriesThatWriteOrBreakTheBoundsNeverReachTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeClickHouseServer.StartAsync(ct);
        await using var brain = await StartAsync(server, ct);
        var clickHouse = brain.Get<IClickHouse>(ClickHouseNames.DefaultNeuron);
        var reached = server.Databases.Count;

        await Assert.ThrowsAsync<ClickHouseQueryException>(() => clickHouse.Query(new("DROP TABLE companies"), ct));
        await Assert.ThrowsAsync<ClickHouseQueryException>(() => clickHouse.Query(new("   "), ct));
        await Assert.ThrowsAsync<ClickHouseQueryException>(() => clickHouse.Query(new("SELECT 1", 0), ct));
        await Assert.ThrowsAsync<ClickHouseQueryException>(() => clickHouse.Query(new("SELECT 1", ClickHouseQuery.MaxRowsLimit + 1), ct));
        Assert.Equal(reached, server.Databases.Count);
    }

    [Fact]
    public void TheGuardRefusesCommentsStackedStatementsAndExternalTableFunctions()
    {
        Assert.Throws<ArgumentException>(() => ClickHouseQueryGuard.Validate("SELECT 1 -- trailing"));
        Assert.Throws<ArgumentException>(() => ClickHouseQueryGuard.Validate("SELECT * FROM url('http://example/x')"));
        Assert.Throws<ArgumentException>(() => ClickHouseQueryGuard.Validate("SELECT 1; SELECT 2"));
        ClickHouseQueryGuard.Validate("WITH x AS (SELECT 1) SELECT * FROM x");
    }

    [Fact]
    public async Task AUserConnectionQueriesTheUsersOwnDatabaseUntilDisconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeClickHouseServer.StartAsync(ct);
        await using var brain = await StartAsync(server, ct);
        var alice = await brain.PersonAsync("alice", ct);
        var key = ConnectionKeys.For(alice.BrainId, "clickhouse", "c1");

        using (alice.AsCaller())
        {
            await brain.Get<IConnection>(key).Connect("connection-string", server.ConnectionString("analytics"));
            Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(key).Read()).Status);

            var result = await brain.Get<IClickHouse>(key).Query(new(Sql), ct);
            Assert.Equal("42", Assert.Single(Assert.Single(result.Rows)));
            Assert.Equal("analytics", server.Databases.Last());

            await brain.Get<IConnection>(key).Disconnect();
            await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IClickHouse>(key).Query(new(Sql), ct));
        }
    }

    [Fact]
    public async Task AnotherBrainCannotQueryAUsersConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeClickHouseServer.StartAsync(ct);
        await using var brain = await StartAsync(server, ct);
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        var key = ConnectionKeys.For(alice.BrainId, "clickhouse", "c1");
        using (alice.AsCaller()) { await brain.Get<IConnection>(key).Connect("connection-string", server.ConnectionString("analytics")); }
        var reached = server.Databases.Count;

        using (bob.AsCaller())
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IClickHouse>(key).Query(new("SELECT 1 AS value"), ct));
        }
        Assert.Equal(reached, server.Databases.Count);
    }

    private static Task<ITestBrain> StartAsync(FakeClickHouseServer server, CancellationToken ct)
        => TestBrain.Create().WithModule<ClickHouseModule>()
            .WithIntegration(ClickHouseModule.Integration, "ConnectionString", server.ConnectionString("deployment"))
            .StartAsync(ct);
}
