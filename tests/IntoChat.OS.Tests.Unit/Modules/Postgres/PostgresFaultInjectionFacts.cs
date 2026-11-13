using DigitalBrain.Capabilities.Connections.Integrations;
using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IntoChat.OS.Modules.Postgres;

public sealed class PostgresFaultInjectionFacts
{
    [Theory]
    [InlineData("08000")] // connection_exception
    [InlineData("08003")] // connection_does_not_exist
    [InlineData("08006")] // connection_failure
    [InlineData("08001")] // sqlclient_unable_to_establish_sqlconnection
    [InlineData("08004")] // sqlserver_rejected_establishment_of_sqlconnection
    [InlineData("08007")] // transaction_resolution_unknown
    [InlineData("28000")] // invalid_authorization_specification
    [InlineData("28P01")] // invalid_password
    [InlineData("3D000")] // invalid_catalog_name (database does not exist)
    [InlineData("57P01")] // admin_shutdown
    [InlineData("57P02")] // crash_shutdown
    [InlineData("57P03")] // cannot_connect_now
    [InlineData("53300")] // too_many_connections
    public void ClassifierTranslatesUnavailableSqlStatesToPostgresUnavailableException(string sqlState)
    {
        var pgException = new PostgresException("test error", "FATAL", "FATAL", sqlState);
        var classified = PostgresExceptionClassifier.Classify(pgException);
        Assert.IsType<PostgresUnavailableException>(classified);
    }

    [Theory]
    [InlineData("23505")] // unique_violation
    [InlineData("42P01")] // undefined_table
    [InlineData("42601")] // syntax_error
    [InlineData("22001")] // string_data_right_truncation
    public void ClassifierTranslatesQuerySqlStatesToPostgresQueryException(string sqlState)
    {
        var pgException = new PostgresException("query error", "ERROR", "ERROR", sqlState);
        var classified = PostgresExceptionClassifier.Classify(pgException);
        Assert.IsType<PostgresQueryException>(classified);
    }

    [Fact]
    public void ClassifierTranslatesGenericNpgsqlExceptionToPostgresUnavailableException()
    {
        var npgsqlException = new NpgsqlException("Socket connection reset");
        var classified = PostgresExceptionClassifier.Classify(npgsqlException);
        Assert.IsType<PostgresUnavailableException>(classified);
    }

    [Fact]
    public async Task HealthCheckReturnsDegradedWhenPingFails()
    {
        var mockProvider = new FaultyPostgresProvider(connected: false);
        var healthCheck = new PostgresHosting.PostgresHealthCheck(mockProvider);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Postgres is unreachable.", result.Description);
    }

    [Fact]
    public async Task HealthCheckReturnsHealthyWhenPingSucceeds()
    {
        var mockProvider = new FaultyPostgresProvider(connected: true);
        var healthCheck = new PostgresHosting.PostgresHealthCheck(mockProvider);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Postgres is reachable.", result.Description);
    }

    [Fact]
    public void RegistryDoesNotThrowOnConstructionWhenUnconfigured()
    {
        var integrations = new FakeIntegrations(IntegrationStatus.Unconfigured);
        var options = Options.Create(new PostgresModuleOptions { ConnectionName = "postgres" });

        // Instantiation must never throw even if unconfigured or unreachable
        var registry = new PostgresSourceRegistry(integrations, options);
        Assert.NotNull(registry);
    }

    [Fact]
    public async Task RegistryThrowsPostgresUnavailableExceptionOnFirstAccessWhenUnconfigured()
    {
        var integrations = new FakeIntegrations(IntegrationStatus.Unconfigured);
        var options = Options.Create(new PostgresModuleOptions { ConnectionName = "postgres" });
        var registry = new PostgresSourceRegistry(integrations, options);

        var ex = await Assert.ThrowsAsync<PostgresUnavailableException>(() => registry.Get(TablePlacement.Shared, TestContext.Current.CancellationToken));
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegistryThrowsPostgresUnavailableExceptionWhenHandleReadThrowsNpgsqlException()
    {
        var integrations = new FaultyIntegrations(new NpgsqlException("Connection refused"));
        var options = Options.Create(new PostgresModuleOptions { ConnectionName = "postgres" });
        var registry = new PostgresSourceRegistry(integrations, options);

        var ex = await Assert.ThrowsAsync<PostgresUnavailableException>(() => registry.Get(TablePlacement.Shared, TestContext.Current.CancellationToken));
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FaultyPostgresProvider(bool connected) : IPostgresProvider
    {
        public Task<PostgresQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<PostgresSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<PostgresConnection> PingAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PostgresConnection(connected, "test_db", "16.0", "postgres"));
    }

    private sealed class FakeIntegrations(IntegrationStatus status) : IModuleIntegrations<PostgresModule>
    {
        public IIntegrationHandle Get(string integrationId) => new FakeHandle(status);

        private sealed class FakeHandle(IntegrationStatus status) : IIntegrationHandle
        {
            public string IntegrationId => "postgres";

            public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new IntegrationSnapshot { IntegrationId = "postgres", Status = status });

            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
                => throw new NotImplementedException();

            public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ReleasedIntegration
                {
                    Snapshot = new IntegrationSnapshot { IntegrationId = "postgres", Status = status },
                    Values = new Dictionary<string, string>()
                });
        }
    }

    private sealed class FaultyIntegrations(Exception ex) : IModuleIntegrations<PostgresModule>
    {
        public IIntegrationHandle Get(string integrationId) => new FaultyHandle(ex);

        private sealed class FaultyHandle(Exception ex) : IIntegrationHandle
        {
            public string IntegrationId => "postgres";

            public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromException<IntegrationSnapshot>(ex);

            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
                => Task.FromException<string>(ex);

            public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
                => Task.FromException<ReleasedIntegration>(ex);
        }
    }
}
