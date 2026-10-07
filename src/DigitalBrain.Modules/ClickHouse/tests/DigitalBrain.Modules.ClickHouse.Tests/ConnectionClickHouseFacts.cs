using DigitalBrain.ClickHouse;
using DigitalBrain.ClickHouse.Query;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.ClickHouse.Tests;

public sealed class ConnectionClickHouseFacts
{
    private const string ConnectionString = "Host=mine;Database=analytics;Username=reader";
    private static readonly string UserKey = ConnectionKeys.For(BrainScope.Create("alice", "brain").Id, "clickhouse", "c1");

    [Fact]
    public async Task AUserConnectionResolvesFromTheNeuronsOwnState()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new RecordingSources();
        await using var brain = await StartAsync(sources);
        await ConnectAsync(brain);
        StampUser();
        Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(UserKey).Read()).Status);

        var result = await brain.Get<IClickHouse>(UserKey).Query(new("SELECT count() AS value FROM companies"), ct);
        Assert.Equal("42", Assert.Single(Assert.Single(result.Rows)));
        Assert.Equal(ConnectionString, sources.Last);

        await brain.Get<IConnection>(UserKey).Disconnect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IClickHouse>(UserKey).Query(new("SELECT count() AS value FROM companies"), ct));
    }

    [Fact]
    public async Task AScriptInAnotherBrainCannotQueryTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new RecordingSources();
        await using var brain = await StartAsync(sources);
        await ConnectAsync(brain);
        CallerContextStamper.Stamp(new() { PrincipalId = "alice", AccountId = "alice", BrainId = "elsewhere", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        await brain.AuthorizeCallerAsync();
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<IClickHouse>(UserKey).Query(new("SELECT 1 AS value"), ct));
        Assert.Null(sources.Last);
    }

    [Fact]
    public async Task TheDefaultKeyStillServesTheDeploymentDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new RecordingSources();
        await using var brain = await StartAsync(sources);
        var connection = await brain.Get<IClickHouse>(ClickHouseNames.DefaultNeuron).ReadConnection(ct);
        Assert.Equal("digitalbrain", connection.Database);
        Assert.Null(sources.Last);
    }

    private static async Task ConnectAsync(ModuleBrain brain)
    {
        StampUser();
        await brain.AuthorizeCallerAsync();
        await brain.Get<IConnection>(UserKey).Connect("connection-string", ConnectionString);
    }

    // A stamp set inside an awaited helper does not flow back to the caller, so facts re-stamp.
    private static void StampUser()
        => CallerContextStamper.Stamp(new() { PrincipalId = "alice", AccountId = "alice", BrainId = "brain", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    private static Task<ModuleBrain> StartAsync(RecordingSources sources)
        => ModuleTest.Create().WithModule<ClickHouseModule>().ConfigureSilo(silo =>
        {
            silo.Configuration[IntegrationSchema.Key("clickhouse", "ConnectionString")] = "Host=localhost;Database=test;Username=default";
            silo.Services.AddSingleton<IClickHouseProvider>(new FakeClickHouseProvider());
            silo.Services.AddSingleton<IClickHouseSourceFactory>(sources);
        }).StartAsync(TestContext.Current.CancellationToken);

    private sealed class RecordingSources : IClickHouseSourceFactory
    {
        public string? Last { get; private set; }
        public IClickHouseProvider ForConnectionString(string connectionString)
        {
            Last = connectionString;
            return new FakeClickHouseProvider();
        }
    }
}
