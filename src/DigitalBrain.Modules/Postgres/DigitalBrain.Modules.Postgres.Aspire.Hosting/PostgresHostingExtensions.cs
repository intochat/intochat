using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Postgres.Aspire.Hosting;

internal static class PostgresHostingExtensions
{
    public static DigitalBrainModuleBuilder<DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting> WithPostgres(
        this DigitalBrainModuleBuilder<DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting> module, string connectionName, PostgresResourceOptions hosting)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(hosting);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hosting.DatabaseName);
        State(module).Enable(new(connectionName, hosting.DatabaseName, hosting.PersistentStorage));
        return module;
    }

    internal static void WithExternalConnection(this DigitalBrainModuleBuilder<DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting> module, string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        State(module).Enable(new(connectionName, null, false));
    }

    private static PostgresHostingState State(DigitalBrainModuleBuilder<DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting> module)
    {
        var state = module.GetOrAddProjection(brain => new PostgresHostingState(brain, module.Resource));
        return state;
    }

    private sealed record HostingConfiguration(string ConnectionName, string? DatabaseName, bool PersistentStorage);

    private sealed class PostgresHostingState(DigitalBrainBuilder brain, IResourceBuilder<DigitalBrainModuleResource> module)
        : DigitalBrainModuleProjection
    {
        private HostingConfiguration? _configuration;
        private IResourceBuilder<PostgresServerResource>? _server;
        private IResourceBuilder<PostgresDatabaseResource>? _database;
        private IResourceBuilder<ParameterResource>? _connection;

        internal void Enable(HostingConfiguration configuration)
        {
            if (_configuration is not null)
            {
                if (_configuration == configuration) { return; }
                throw new InvalidOperationException("Postgres hosting is already configured. Configure it once before referencing the brain.");
            }

            // Publish mode never runs the container: the external secret parameter carries the
            // manually provisioned database, and without an admin connection no provisioner registers.
            if (configuration.DatabaseName is { } databaseName && !brain.ApplicationBuilder.ExecutionContext.IsPublishMode)
            {
                var server = brain.ApplicationBuilder.AddPostgres(brain.ResourceName("postgres-server"))
                    .WithParentRelationship(module)
                    .WithRepl();
                if (configuration.PersistentStorage) { server.WithDataVolume().WithLifetime(ContainerLifetime.Persistent); }
                _server = server;
                _database = server.AddDatabase(brain.ResourceName("postgres-database"), databaseName);
            }
            else
            {
                _connection = brain.ApplicationBuilder.AddParameter(brain.ResourceName("postgres-connection"), () =>
                    brain.ApplicationBuilder.Configuration["Parameters:postgres-connection"]
                    ?? brain.ApplicationBuilder.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")]
                    ?? throw new InvalidOperationException($"Connection string '{configuration.ConnectionName}' is required."), secret: true)
                    .WithParentRelationship(module);
            }
            _configuration = configuration;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_configuration is null) { return; }
            if (_database is not null)
            {
                builder.WithEnvironment(DigitalBrain.DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString")), _database.Resource.ConnectionStringExpression)
                    .WithAnnotation(new WaitAnnotation(_database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
                if (_server is not null)
                {
                    builder.WithEnvironment(DigitalBrain.DigitalBrainConfiguration.EnvironmentName(DigitalBrain.DigitalBrainConfiguration.ModuleOptionsSection("postgres"), "ProvisionPerBrain"), "true");
                    var server = _server.Resource;
                    builder.WithEnvironment(context =>
                        context.EnvironmentVariables[DigitalBrain.DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "AdminConnection"))] = server.ConnectionStringExpression);
                }
            }
            else if (_connection is not null)
            {
                builder.WithEnvironment(DigitalBrain.DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString")), _connection);
            }
        }
    }
}
