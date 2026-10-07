using DigitalBrain.Deployment;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using Pulumi;

namespace DigitalBrain.ClickHouse;

// Runs the manifest's ClickHouse container as an internal service with its data on Azure Files.
// Seed scripts the AppHost copies into a local container do not travel with the manifest.
public sealed class ClickHouseDeployment : ModuleDeployment<ClickHouseModule>
{
    private const int HttpPort = 8123;

    public override void Deploy(ModuleDeploymentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var server = context.Manifest.ServingContainer(context.Runtime, ClickHouseRegistration.DefaultConnectionName);
        var databaseKey = DigitalBrainConfiguration.EnvironmentName(
            DigitalBrainConfiguration.ModuleOptionsSection("clickhouse"), nameof(ClickHouseModuleOptions.DatabaseName));
        var database = context.Manifest.Environment(context.Runtime).GetValueOrDefault(databaseKey)
            ?? new ClickHouseModuleOptions().DatabaseName;
        var app = context.AddService(new ServiceDefinition("clickhouse", context.Manifest.Image(server), HttpPort)
        {
            Transport = "http",
            AllowInsecure = true,
            Environment = new Dictionary<string, Input<string>>
            {
                ["CLICKHOUSE_USER"] = "default",
                ["CLICKHOUSE_DB"] = context.Values.Resolve(database),
            },
            Secrets = new Dictionary<string, Input<string>> { ["CLICKHOUSE_PASSWORD"] = context.Parameter(server + "-password") },
            DataPath = "/var/lib/clickhouse",
            Cpu = 2,
            Memory = "4Gi",
        });
        context.Provide(server, "bindings.http.host", context.InternalHost(app));
        context.Provide(server, "bindings.http.port", "80");
    }
}
