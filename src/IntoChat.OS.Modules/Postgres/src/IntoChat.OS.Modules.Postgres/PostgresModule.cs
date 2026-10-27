using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Postgres.Tables.Signals;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Postgres;

public sealed class PostgresModule() : Module<PostgresModuleOptions>(new ModuleId("postgres"))
{
    // R4-04: what the module needs from its host (PostgresNeurons). Configure keeps the registrations of the services the table
    // neuron takes and of the neurons that are still grains (the server and the index) until the Framework leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest => PostgresNeurons.Manifest;

    public override Type? Neurons => typeof(PostgresNeurons);

    public override bool RequiresContainer => true;

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Postgres.IPostgresServer).Assembly;

    public static IntegrationSchema Integration => IntegrationSchema.For("postgres", "Postgres").RequiresSecret("ConnectionString").OffersSecret("AdminConnection");
    public static ConnectorDefinition Connector => new("postgres", "Postgres", ["connection-string"], ["postgres"]) { Neurons = [typeof(IPostgresServer), typeof(ITable)], Signals = [typeof(TableDefined), typeof(RowUpserted), typeof(RowDeleted)] };

    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration) => services.AddPostgres();
}

// The DigitalBrain module of Postgres (06-05b): its assembly's [Neuron] classes (the app table) run on the runtime's host grains.
[DigitalBrain.Module("postgres")]
public sealed class PostgresNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<PostgresModuleOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId("postgres")))],
        Secrets: []);
}
