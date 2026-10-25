using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Sdk.Platform.Integrations;

namespace IntoChat.OS.Modules.ClickHouse.Aspire.Hosting;

public static class ClickHouseHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.ClickHouse.Aspire.Hosting.ClickHouseModuleHosting> WithClickHouse(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.ClickHouse.Aspire.Hosting.ClickHouseModuleHosting> module,
        Action<ClickHouseHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var options = new ClickHouseHostingOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseName);
        State(module).Enable(options);
        return module;
    }

    private static ClickHouseHostingState State(IntoChatOSModuleBuilder<IntoChat.OS.Modules.ClickHouse.Aspire.Hosting.ClickHouseModuleHosting> module)
    {
        var state = module.GetOrAddProjection(static brain => new ClickHouseHostingState(brain));

        return state;
    }

    internal static string ReadSeed(string name)
    {
        var resourceName = $"Seeds/{name}.sql";
        using var stream = typeof(ClickHouseHostingExtensions).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"ClickHouse seed '{name}' is not an embedded resource under Seeds/.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class ClickHouseHostingState(IntoChatOSBuilder brain) : IntoChatOSModuleProjection
    {
        private IResourceBuilder<ClickHouseServerResource>? _server;
        private IResourceBuilder<ClickHouseDatabaseResource>? _database;
        private bool _enabled;

        internal void Enable(ClickHouseHostingOptions options)
        {
            if (_enabled)
            {
                return;
            }

            var builder = brain.ApplicationBuilder;
            _server = builder.AddClickHouse(brain.ResourceName(ClickHouseResourceNames.Server))
                .WithParentRelationship(brain.Resource)
                .WithUrlForEndpoint("http", static endpoint => new ResourceUrlAnnotation
                {
                    Url = "/play",
                    DisplayText = "ClickHouse Play",
                    Endpoint = endpoint,
                });
            if (options.PersistentStorage) { _server.WithDataVolume().WithLifetime(ContainerLifetime.Persistent); }
            _database = _server.AddDatabase(brain.ResourceName(ClickHouseResourceNames.Database), options.DatabaseName);

            if (options.Seeds.Count > 0)
            {
                // Init scripts run alphabetically on an empty data dir; the numeric prefix keeps the order added.
                var seeds = options.Seeds.Select((name, index) => (ContainerFileSystemItem)new ContainerFile
                {
                    Name = $"{(index + 1).ToString("000", CultureInfo.InvariantCulture)}-{name}.sql",
                    Contents = ReadSeed(name),
                }).ToArray();
                _server.WithContainerFiles("/docker-entrypoint-initdb.d", seeds);
            }

            if (options.AlwaysRunInitScripts)
            {
                _server.WithEnvironment("CLICKHOUSE_ALWAYS_RUN_INITDB_SCRIPTS", "1");
            }

            _enabled = true;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (!_enabled || _database is null)
            {
                return;
            }

            // Waiting on the database resource covers server health and the CREATE DATABASE step.
            builder
                .WithEnvironment(IntoChat.OS.Sdk.Configuration.IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("clickhouse", "ConnectionString")), _database.Resource.ConnectionStringExpression)
                .WithAnnotation(new WaitAnnotation(_database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
        }
    }
}
