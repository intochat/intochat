using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Modules.Postgres.Tests.Integration;

public sealed class PostgresFacts(PostgresHost host)
{
    [Fact]
    public void APostgresOutageIsAServiceUnavailableRefusal()
        => Assert.IsAssignableFrom<ServiceUnavailableException>(new PostgresUnavailableException("down"));

    [Fact]
    public async Task ParametersAreBoundNotInterpolated()
    {
        var brain = host.Brain;
        var result = await brain.Get<IPostgresServer>("default")
            .Query(new("select id from people where country = $1", 10, [new("country", "u's; drop table people")]));
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task MoreThanSixteenParametersAreRejected()
    {
        var brain = host.Brain;
        var many = Enumerable.Range(0, 17).Select(i => new PostgresParameter($"p{i}", "x")).ToArray();
        await Assert.ThrowsAsync<PostgresQueryException>(() => brain.Get<IPostgresServer>("default").Query(new("select 1", 10, many)));
    }

    [Fact]
    public async Task QueryReturnsTypedRowsThroughTheDefaultGrainType()
    {
        var brain = host.Brain;
        var result = await brain.Get<IPostgresServer>("default").Query(new(" select id from people; ", 10));
        Assert.Equal(1, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Equal("id", Assert.Single(result.Columns).Name);
        Assert.Equal("7", Assert.Single(Assert.Single(result.Rows)));
    }

    [Fact]
    public async Task InvalidQueriesAreRejectedBeforeDatabaseAccess()
    {
        var brain = host.Brain;
        var postgres = brain.Get<IPostgresServer>("default");
        await Assert.ThrowsAsync<PostgresQueryException>(() => postgres.Query(new(" ")));
        await Assert.ThrowsAsync<PostgresQueryException>(() => postgres.Query(new("delete from people")));
        await Assert.ThrowsAsync<PostgresQueryException>(() => postgres.Query(new("select 1", 0)));
        await Assert.ThrowsAsync<PostgresQueryException>(() => postgres.Query(new("select 1", 1001)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SchemaAndConnectionAreAvailableThroughTheNeuron()
    {
        await using var brain = await StartAsync("reporting");
        var postgres = brain.Get<IPostgresServer>("default");
        var schema = await postgres.ReadSchema(new(" reporting.people "));
        Assert.Equal("reporting.people", Assert.Single(schema.Tables).Name);
        var connection = await postgres.ReadConnection();
        Assert.True(connection.Connected);
        Assert.Equal("sample", connection.Database);
    }

    [Theory]
    [InlineData("postgresql://reader:p%40ss@localhost:5433/sample?sslmode=require")]
    [InlineData("Host=localhost;Port=5433;Database=sample;Username=reader;Password=p@ss")]
    public void ConnectionFormatsAreParsedAndSensitiveDiagnosticsAreDisabled(string connection)
    {
        var settings = PostgresConnectionSettings.Parse(connection);
        Assert.Equal("sample", settings.Database);
        Assert.Equal("reader", settings.Username);
        Assert.Equal("p@ss", settings.Password);
        Assert.Equal(5433, settings.Port);
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.LogParameters);
        Assert.False(settings.PersistSecurityInfo);
    }

    [Fact]
    public void InvalidConnectionDoesNotLeakCredentials()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgresConnectionSettings.Parse("Password=secret-value"));
        Assert.DoesNotContain("secret-value", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Password=secret-value")]
    public async Task InvalidCredentialIsRejectedWhenTheSourceIsRequested(string? connection)
    {
        await using var sources = new PostgresSourceRegistry(new Credential(connection), Options.Create(new PostgresModuleOptions()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sources.Get(DigitalBrain.Postgres.Tables.TablePlacement.Shared, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret-value", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "text", "null")]
    [InlineData("t", "boolean", "true")]
    [InlineData("f", "boolean", "false")]
    [InlineData("42", "number", "42")]
    [InlineData("9007199254740993", "number", "\"9007199254740993\"")]
    [InlineData("1.234567890123456789", "number", "\"1.234567890123456789\"")]
    [InlineData("NaN", "number", "\"NaN\"")]
    public void CellsPreserveNullsBooleansAndHighPrecisionValues(string? value, string type, string expected)
        => Assert.Equal(expected, PostgresCells.ToCell(value, type));

    [Fact]
    public async Task InvalidSchemaNameIsRejected()
    {
        var brain = host.Brain;
        await Assert.ThrowsAsync<PostgresQueryException>(() => brain.Get<IPostgresServer>("default").ReadSchema(new("people; drop table people")));
    }

    [Fact]
    public async Task TheBrainIsReadyWhenTheDatabaseAnswers()
    {
        var brain = host.Brain;
        using var ready = await brain.HttpClient.GetAsync("/ready", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
    }

    private static Task<ITestBrain> StartAsync(string connectionName = "postgres")
        => Compose(TestBrain.Create(), connectionName).StartAsync(TestContext.Current.CancellationToken);

    internal static TestBrain Compose(TestBrain brain, string connectionName = "postgres")
        => brain.WithModule<PostgresModule, PostgresModuleOptions>(module => module.WithConnection(connectionName))
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=sample;Username=reader")
            // Rule 4: a wire-level fake would need a loopback Postgres; the provider stands in for the database.
            .ConfigureServices(services => services.AddSingleton<IPostgresProvider, PostgresTestControls>());

    private sealed class Credential(string? connection) : IModuleIntegrations<PostgresModule>, IIntegrationHandle
    {
        public string IntegrationId => "postgres";
        public IIntegrationHandle Get(string integrationId) => this;
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ReleasedIntegration
            {
                Snapshot = new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready },
                Values = new() { ["ConnectionString"] = connection ?? throw new InvalidOperationException("Missing integration credential.") },
            });
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
