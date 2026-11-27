using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Modules.ClickHouse;
using IntoChat.OS.Modules.Postgres;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Generic manifest-driven Aspire hosting projector.
/// Inspects module manifests and configuration, projecting backing infrastructure resources
/// (Postgres, ClickHouse, containers) and wiring secrets and environment variables.
/// </summary>
public static class ManifestHostingProjector
{
    public static IntoChatOSBuilder Project(IntoChatOSBuilder brain, Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(moduleType);

        if (moduleType.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not DigitalBrain.Modules.ModuleManifest manifest
            || moduleType.GetCustomAttribute<DigitalBrain.Modules.ModuleAttribute>()?.Id is not { Length: > 0 } moduleId)
        {
            return brain;
        }

        ProjectInfrastructure(brain, moduleId, manifest);
        return brain;
    }

    private static void ProjectInfrastructure(IntoChatOSBuilder brain, string moduleId, DigitalBrain.Modules.ModuleManifest manifest)
    {
        var config = brain.GetModuleConfiguration(moduleId);

        foreach (var need in manifest.Needs)
        {
            switch (need.Kind)
            {
                case DigitalBrain.Modules.NeedKind.State when string.Equals(need.Name, "postgres", StringComparison.OrdinalIgnoreCase):
                    ProjectPostgres(brain, config);
                    break;
                case DigitalBrain.Modules.NeedKind.State when string.Equals(need.Name, "clickhouse", StringComparison.OrdinalIgnoreCase):
                    ProjectClickHouse(brain, config);
                    break;
                case DigitalBrain.Modules.NeedKind.Sandbox when need.Image is { Length: > 0 } image && need.Name is { Length: > 0 } name:
                    var moduleNode = brain.GetOrAddModuleNode(moduleId);
                    var container = brain.ApplicationBuilder.AddContainer(brain.ResourceName(name), image)
                        .WithParentRelationship(moduleNode);
                    brain.RequireHealthyBeforeStart(container.Resource);
                    break;
            }
        }
    }

    private static void ProjectPostgres(IntoChatOSBuilder brain, IConfiguration config)
    {
        var options = config.GetModuleOptions<PostgresModuleOptions>("postgres");
        var moduleNode = brain.GetOrAddModuleNode("postgres");

        if (options.Hosting.Enabled && !string.IsNullOrWhiteSpace(options.Hosting.DatabaseName) && !brain.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            var server = brain.ApplicationBuilder.AddPostgres(brain.ResourceName("postgres-server"))
                .WithParentRelationship(moduleNode)
                .WithRepl();

            if (options.Hosting.PersistentStorage)
            {
                server.WithDataVolume(LegacyNames.DataVolume(brain.ApplicationBuilder, LegacyNames.Resource("postgres-server"))).WithLifetime(ContainerLifetime.Persistent);
            }

            var database = server.AddDatabase(brain.ResourceName("postgres-database"), options.Hosting.DatabaseName);

            brain.AddProjection(new PostgresProjection(server, database));
        }
        else
        {
            var connection = brain.ApplicationBuilder.AddParameter(brain.ResourceName("postgres-connection"), () =>
                HostConfiguration.Parameter(brain.ApplicationBuilder.Configuration, "postgres-connection")
                ?? HostConfiguration.IntegrationValue(brain.ApplicationBuilder.Configuration, "postgres", "ConnectionString")
                ?? throw new InvalidOperationException($"Connection string '{options.ConnectionName}' is required."), secret: true)
                .WithParentRelationship(moduleNode);

            brain.AddProjection(new PostgresExternalProjection(connection));
        }
    }

    private static void ProjectClickHouse(IntoChatOSBuilder brain, IConfiguration config)
    {
        var options = config.GetModuleOptions<ClickHouseModuleOptions>("clickhouse");
        if (!options.Hosting.Enabled)
        {
            return;
        }

        var databaseName = options.DatabaseName;
        var server = brain.ApplicationBuilder.AddClickHouse(brain.ResourceName("clickhouse-server"))
            .WithParentRelationship(brain.Resource)
            .WithUrlForEndpoint("http", static endpoint => new ResourceUrlAnnotation
            {
                Url = "/play",
                DisplayText = "ClickHouse Play",
                Endpoint = endpoint,
            });

        if (options.Hosting.PersistentStorage)
        {
            server.WithDataVolume(LegacyNames.DataVolume(brain.ApplicationBuilder, LegacyNames.Resource("clickhouse-server"))).WithLifetime(ContainerLifetime.Persistent);
        }

        var database = server.AddDatabase(brain.ResourceName("clickhouse-database"), databaseName);
        if (options.Hosting.AlwaysRunInitScripts)
        {
            server.WithEnvironment("CLICKHOUSE_ALWAYS_RUN_INITDB_SCRIPTS", "1");
        }

        brain.AddProjection(new ClickHouseProjection(database));
    }

    private sealed class PostgresProjection(IResourceBuilder<PostgresServerResource> server, IResourceBuilder<PostgresDatabaseResource> database)
        : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var connectionStringKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString"));

            builder.WithEnvironment(connectionStringKey, database.Resource.ConnectionStringExpression)
                .WithEnvironment(DigitalBrain.Modules.Configuration.ModuleConfiguration.EnvironmentName(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("postgres"), "Isolation"), "DatabasePerBrain")
                .WithAnnotation(new WaitAnnotation(database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));

            var serverResource = server.Resource;
            var adminKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "AdminConnection"));

            builder.WithEnvironment(context =>
            {
                context.EnvironmentVariables[adminKey] = serverResource.ConnectionStringExpression;
            });
        }
    }

    private sealed class PostgresExternalProjection(IResourceBuilder<ParameterResource> connection)
        : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var connectionStringKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString"));

            builder.WithEnvironment(connectionStringKey, connection);
        }
    }

    private sealed class ClickHouseProjection(IResourceBuilder<ClickHouseDatabaseResource> database) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var connectionStringKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("clickhouse", "ConnectionString"));
            var manifestSecretKey = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleSection("clickhouse") + ":Secrets:connection-string");

            builder
                .WithEnvironment(connectionStringKey, database.Resource.ConnectionStringExpression)
                .WithEnvironment(manifestSecretKey, database.Resource.ConnectionStringExpression)
                .WithAnnotation(new WaitAnnotation(database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
        }
    }
}
