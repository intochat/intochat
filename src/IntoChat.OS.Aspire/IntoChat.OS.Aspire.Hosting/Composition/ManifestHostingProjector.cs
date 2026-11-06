using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.ClickHouse;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Qdrant;
using IntoChat.OS.Modules.Supabase;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Generic manifest-driven Aspire hosting projector.
/// Inspects module manifests and configuration, projecting backing infrastructure resources
/// (Postgres, Qdrant, ClickHouse, containers) and wiring secrets and environment variables.
/// </summary>
public static class ManifestHostingProjector
{
    public static IntoChatOSBuilder Project(IntoChatOSBuilder brain, Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(moduleType);

        if (moduleType.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not DigitalBrain.ModuleManifest manifest
            || moduleType.GetCustomAttribute<DigitalBrain.ModuleAttribute>()?.Id is not { Length: > 0 } moduleId)
        {
            return brain;
        }

        ProjectInfrastructure(brain, moduleId, manifest);
        return brain;
    }

    private static void ProjectInfrastructure(IntoChatOSBuilder brain, string moduleId, DigitalBrain.ModuleManifest manifest)
    {
        var config = brain.GetModuleConfiguration(moduleId);

        switch (moduleId)
        {
            case "postgres":
                ProjectPostgres(brain, config);
                break;
            case "qdrant":
                ProjectQdrant(brain, config);
                break;
            case "clickhouse":
                ProjectClickHouse(brain, config);
                break;
            case "supabase":
                ProjectSupabase(brain, config);
                break;
            default:
                ProjectGenericNeeds(brain, moduleId, manifest);
                break;
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
                server.WithDataVolume().WithLifetime(ContainerLifetime.Persistent);
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

    private static void ProjectQdrant(IntoChatOSBuilder brain, IConfiguration config)
    {
        var options = config.GetModuleOptions<QdrantModuleOptions>("qdrant");
        if (!options.Host)
        {
            return;
        }

        var moduleNode = brain.GetOrAddModuleNode("qdrant");
        var qdrant = brain.ApplicationBuilder.AddQdrant(brain.ResourceName("qdrant-server"))
            .WithParentRelationship(moduleNode)
            .WithDataVolume()
            .WithLifetime(ContainerLifetime.Persistent);

        brain.AddProjection(new QdrantProjection(qdrant));
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
            server.WithDataVolume().WithLifetime(ContainerLifetime.Persistent);
        }

        var database = server.AddDatabase(brain.ResourceName("clickhouse-database"), databaseName);
        if (options.Hosting.AlwaysRunInitScripts)
        {
            server.WithEnvironment("CLICKHOUSE_ALWAYS_RUN_INITDB_SCRIPTS", "1");
        }

        brain.AddProjection(new ClickHouseProjection(database));
    }

    private static void ProjectSupabase(IntoChatOSBuilder brain, IConfiguration config)
    {
        var options = config.GetModuleOptions<SupabaseModuleOptions>("supabase");
        if (!options.UseBackingStore)
        {
            return;
        }

        var moduleNode = brain.GetOrAddModuleNode("supabase");
        if (options.Hosting.Kind == SupabaseHostKind.External)
        {
            var parameterName = options.ConnectionName;
            var connection = brain.ApplicationBuilder.AddParameter(brain.ResourceName("supabase-connection"), () =>
                HostConfiguration.Parameter(brain.ApplicationBuilder.Configuration, parameterName)
                ?? HostConfiguration.IntegrationValue(brain.ApplicationBuilder.Configuration, "supabase", "ConnectionString")
                ?? "", secret: true)
                .WithParentRelationship(moduleNode);

            brain.AddProjection(new SupabaseExternalProjection(connection));
            return;
        }

        var server = brain.ApplicationBuilder.AddPostgres(brain.ResourceName("supabase-postgres"))
            .WithParentRelationship(moduleNode);
        var database = server.AddDatabase(brain.ResourceName("supabase-database"), options.ConnectionName);
        brain.AddProjection(new SupabasePostgresProjection(database));
    }

    private static void ProjectGenericNeeds(IntoChatOSBuilder brain, string moduleId, DigitalBrain.ModuleManifest manifest)
    {
        var moduleNode = brain.GetOrAddModuleNode(moduleId);
        foreach (var need in manifest.Needs)
        {
            if (need.Kind == DigitalBrain.NeedKind.Sandbox && need.Image is { Length: > 0 } image && need.Name is { Length: > 0 } name)
            {
                var container = brain.ApplicationBuilder.AddContainer(brain.ResourceName(name), image)
                    .WithParentRelationship(moduleNode);
                brain.RequireHealthyBeforeStart(container.Resource);
            }
        }
    }

    private sealed class PostgresProjection(IResourceBuilder<PostgresServerResource> server, IResourceBuilder<PostgresDatabaseResource> database)
        : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var connectionStringKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString"));
            var manifestSecretKey = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleSection("postgres") + ":Secrets:connection-string");

            builder.WithEnvironment(connectionStringKey, database.Resource.ConnectionStringExpression)
                .WithEnvironment(manifestSecretKey, database.Resource.ConnectionStringExpression)
                .WithEnvironment(DigitalBrain.Modules.Configuration.ModuleConfiguration.EnvironmentName(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("postgres"), "Isolation"), "DatabasePerBrain")
                .WithAnnotation(new WaitAnnotation(database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));

            var serverResource = server.Resource;
            var adminKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "AdminConnection"));
            var manifestAdminKey = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleSection("postgres") + ":Secrets:admin-connection");

            builder.WithEnvironment(context =>
            {
                context.EnvironmentVariables[adminKey] = serverResource.ConnectionStringExpression;
                context.EnvironmentVariables[manifestAdminKey] = serverResource.ConnectionStringExpression;
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
            var manifestSecretKey = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleSection("postgres") + ":Secrets:connection-string");

            builder.WithEnvironment(connectionStringKey, connection)
                .WithEnvironment(manifestSecretKey, connection);
        }
    }

    private sealed class QdrantProjection(IResourceBuilder<QdrantServerResource> qdrant) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder
                .WithReference(qdrant, connectionName: "qdrant")
                .WithAnnotation(new WaitAnnotation(qdrant.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
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

    private sealed class SupabaseExternalProjection(IResourceBuilder<ParameterResource> connection) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var connectionStringKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString"));
            var manifestSecretKey = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleSection("supabase") + ":Secrets:connection-string");

            builder.WithEnvironment(DigitalBrain.Modules.Configuration.ModuleConfiguration.EnvironmentName(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("supabase"), nameof(SupabaseModuleOptions.UseBackingStore)), "true")
                .WithEnvironment(connectionStringKey, connection)
                .WithEnvironment(manifestSecretKey, connection);
        }
    }

    private sealed class SupabasePostgresProjection(IResourceBuilder<PostgresDatabaseResource> database) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var connectionStringKey = ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString"));
            var manifestSecretKey = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleSection("supabase") + ":Secrets:connection-string");

            builder.WithEnvironment(DigitalBrain.Modules.Configuration.ModuleConfiguration.EnvironmentName(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("supabase"), nameof(SupabaseModuleOptions.UseBackingStore)), "true")
                .WithEnvironment(connectionStringKey, database.Resource.ConnectionStringExpression)
                .WithEnvironment(manifestSecretKey, database.Resource.ConnectionStringExpression)
                .WithAnnotation(new WaitAnnotation(database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
        }
    }
}
