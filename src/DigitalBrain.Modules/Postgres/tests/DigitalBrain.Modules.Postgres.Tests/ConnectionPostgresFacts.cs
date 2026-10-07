using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework;
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
        Stamp();
        await brain.AuthorizeCallerAsync();
        var other = BrainScope.Create("alice", "elsewhere").Id;
        var key = ConnectionKeys.For(other, "postgres", "c1");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>(key).Query(new("select id from people", 10)));
    }

    [Fact]
    public async Task TheDefaultKeyStaysPlatformOnly()
    {
        await using var brain = await StartAsync();
        Stamp();
        await brain.AuthorizeCallerAsync();
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IPostgresServer>("default").Query(new("select id from people", 10)));
    }

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
            silo.Services.AddSingleton<IModuleConnectionCredentials<PostgresModule>>(new Credentials());
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

    private sealed class Credentials : IModuleConnectionCredentials<PostgresModule>
    {
        public Task<string> ReadConnectionAsync(string connectorId, string connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(("postgres", "c1"), (connectorId, connectionId));
            return Task.FromResult(ConnectionString);
        }
        public Task<DigitalBrain.Framework.SecretRef> WriteSecretAsync(string connectionKey, string field, string displayName, string value, CancellationToken cancellationToken = default, string? targetBrainId = null) => throw new NotSupportedException();
        public Task ConnectAsync(string connectionKey, string authMethodId, DigitalBrain.Framework.SecretRef credential, CancellationToken cancellationToken = default, string? targetBrainId = null) => throw new NotSupportedException();
        public Task<bool> IsConnectedAsync(string connectionKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

internal sealed class MineProvider : IPostgresProvider
{
    public Task<PostgresQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken)
        => Task.FromResult(new PostgresQueryResult([new("id", "int4", "number")], [["42"]], 1, false, 0));
    public Task<PostgresSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PostgresConnection> PingAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}
