using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Postgres.Tests;

public sealed class ConnectionPostgresFacts
{
    private const string ConnectionString = "Host=localhost;Database=mine;Username=reader";

    [Fact]
    public async Task AScriptInItsOwnBrainQueriesItsConnectedDatabase()
    {
        await using var brain = await StartAsync();
        await ConnectAsync(brain);
        Stamp();
        await brain.AuthorizeCallerAsync();
        var key = ConnectionKeys.For(BrainScope.CurrentId(), "postgres", "c1");
        var result = await brain.Get<IPostgresServer>(key).Query(new("select id from people", 10));
        Assert.Equal("42", Assert.Single(Assert.Single(result.Rows)));
    }

    [Fact]
    public async Task AScriptMayNotAddressAnotherBrainsConnection()
    {
        await using var brain = await StartAsync();
        await ConnectAsync(brain);
        Stamp();
        await brain.AuthorizeCallerAsync();
        var other = BrainScope.Create("alice", "elsewhere").Id;
        var key = ConnectionKeys.For(other, "postgres", "c1");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>(key).Query(new("select id from people", 10)));
    }

    [Fact]
    public async Task TheDefaultKeyRefusesABrainMember()
    {
        await using var brain = await StartAsync();
        Stamp();
        await brain.AuthorizeCallerAsync();
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>("default").Query(new("select id from people", 10)));
    }

    [Fact]
    public async Task AUserConnectionResolvesFromTheNeuronsOwnState()
    {
        await using var brain = await StartAsync();
        await ConnectAsync(brain);
        StampUser();
        var view = await brain.Get<IConnection>(UserKey).Read();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.DoesNotContain("Database=mine", view.ToString(), StringComparison.Ordinal);
        await brain.Get<IConnection>(UserKey).Disconnect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IPostgresServer>(UserKey).Query(new("select id from people", 10)));
    }

    [Fact]
    public async Task AProbeAsksTheConnectedDatabase()
    {
        await using var brain = await StartAsync();
        await ConnectAsync(brain);
        StampUser();
        var view = await brain.Get<IConnection>(UserKey).Probe();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.NotNull(view.LastProbedAt);
    }

    [Fact]
    public async Task TheDefaultKeyIsNoConnection()
    {
        await using var brain = await StartAsync();
        Orleans.Runtime.RequestContext.Clear();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IConnection>("default").Read());
        Assert.Contains("connection grain key", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IConnection>("default").Connect("connection-string", ConnectionString));
        StampUser();
        await brain.AuthorizeCallerAsync();
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IConnection>("default").Connect("connection-string", ConnectionString));
        Orleans.Runtime.RequestContext.Clear();
        // The deployment's database still answers on the same key.
        Assert.Equal("7", Assert.Single(Assert.Single((await brain.Get<IPostgresServer>("default").Query(new("select id from people", 10))).Rows)));
    }

    private static readonly string UserKey = ConnectionKeys.For(BrainScope.Create("alice", "brain").Id, "postgres", "c1");

    private static async Task ConnectAsync(ModuleBrain brain)
    {
        StampUser();
        await brain.AuthorizeCallerAsync();
        await brain.Get<IConnection>(ConnectionKeys.For(BrainScope.CurrentId(), "postgres", "c1")).Connect("connection-string", ConnectionString);
    }

    // A stamp set inside an awaited helper does not flow back to the caller, so facts re-stamp.
    private static void StampUser()
        => CallerContextStamper.Stamp(new() { PrincipalId = "alice", AccountId = "alice", BrainId = "brain", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    private static void Stamp()
        => CallerContextStamper.Stamp(new()
        {
            PrincipalId = "alice",
            AccountId = "alice",
            BrainId = "brain",
            Kind = CallerKind.App,
            AppId = "install",
            StampedBy = TrustedEdge.AppProxy
        });

    private static Task<ModuleBrain> StartAsync()
        => ModuleTest.Create().WithModule<PostgresModule>().ConfigureSilo(silo =>
        {
            silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader";
            silo.Services.AddSingleton<IPostgresProvider, PostgresTestControls>();
            silo.Services.AddSingleton<IConnectionPostgresProviderFactory>(new Factory());
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
