using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration;

public sealed class ConnectionPostgresFacts
{
    private const string ConnectionString = "Host=localhost;Database=mine;Username=reader";

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppInItsOwnBrainQueriesItsConnectedDatabase()
    {
        await using var brain = await StartAsync();
        var key = await ConnectAsync(brain, brain.Owner);
        using var app = brain.Owner.AsApp("install");
        var result = await brain.Get<IPostgresServer>(key).Query(new("select id from people", 10));
        Assert.Equal("42", Assert.Single(Assert.Single(result.Rows)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppMayNotAddressAnotherBrainsConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync();
        using var bob = await brain.PersonAsync("bob", ct);
        var key = await ConnectAsync(brain, bob);
        using var app = brain.Owner.AsApp("install");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>(key).Query(new("select id from people", 10)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheDefaultKeyRefusesAnApp()
    {
        await using var brain = await StartAsync();
        using var app = brain.Owner.AsApp("install");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>("default").Query(new("select id from people", 10)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADisconnectedConnectionNoLongerQueriesAndNeverShowsItsConnectionString()
    {
        await using var brain = await StartAsync();
        var key = await ConnectAsync(brain, brain.Owner);
        using var owner = brain.Owner.AsCaller();
        var view = await brain.Get<IConnection>(key).Read();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.DoesNotContain("Database=mine", view.ToString(), StringComparison.Ordinal);
        await brain.Get<IConnection>(key).Disconnect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IPostgresServer>(key).Query(new("select id from people", 10)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProbeAsksTheConnectedDatabase()
    {
        await using var brain = await StartAsync();
        var key = await ConnectAsync(brain, brain.Owner);
        using var owner = brain.Owner.AsCaller();
        var view = await brain.Get<IConnection>(key).Probe();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.NotNull(view.LastProbedAt);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheDefaultKeyIsNoConnection()
    {
        await using var brain = await StartAsync();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IConnection>("default").Read());
        Assert.Contains("connection grain key", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IConnection>("default").Connect("connection-string", ConnectionString));
        using (brain.Owner.AsCaller())
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IConnection>("default").Connect("connection-string", ConnectionString));
        }
        // The deployment's database still answers host code on the same key.
        Assert.Equal("7", Assert.Single(Assert.Single((await brain.Get<IPostgresServer>("default").Query(new("select id from people", 10))).Rows)));
    }

    private static async Task<string> ConnectAsync(ITestBrain brain, Person person)
    {
        var key = ConnectionKeys.For(person.BrainId, "postgres", "c1");
        using var caller = person.AsCaller();
        await brain.Get<IConnection>(key).Connect("connection-string", ConnectionString);
        return key;
    }

    private static Task<ITestBrain> StartAsync()
        => TestBrain.Create().WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=platform;Username=reader")
            // Rule 4: a wire-level fake would need a loopback Postgres; the providers stand in for the databases.
            .ConfigureServices(services =>
            {
                services.AddSingleton<IPostgresProvider, PostgresTestControls>();
                services.AddSingleton<IConnectionPostgresProviderFactory>(new Factory());
            }).StartAsync(TestContext.Current.CancellationToken);

    private sealed class Factory : IConnectionPostgresProviderFactory
    {
        public IPostgresProvider ForConnectionString(string connectionString)
        {
            Assert.Equal(ConnectionString, connectionString);
            return new MineProvider();
        }
    }
}

internal sealed class MineProvider : IPostgresProvider
{
    public Task<PostgresQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken)
        => Task.FromResult(new PostgresQueryResult([new("id", "int4", "number")], [["42"]], 1, false, 0));
    public Task<PostgresSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PostgresConnection> PingAsync(CancellationToken cancellationToken) => Task.FromResult(new PostgresConnection(true, "mine", "17", "Npgsql"));
}
