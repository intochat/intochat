using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Orleans.Hosting;

namespace DigitalBrain.Postgres;

public sealed class PostgresModule() : Module<PostgresModuleOptions>(new ModuleId("postgres"))
{
    public static IntegrationSchema Integration => IntegrationSchema.For("postgres", "Postgres").RequiresSecret("ConnectionString").OffersSecret("AdminConnection");
    public static ConnectorDefinition Connector => new("postgres", "Postgres", ["connection-string"], ["postgres"]) { Neurons = [typeof(IPostgres), typeof(IPostgresTable)], Signals = [typeof(TableDefined), typeof(RowUpserted), typeof(RowDeleted)] };

    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public override void Configure(ISiloBuilder silo) => silo.AddPostgres();
}
