using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Postgres.Tables.Signals;
using Orleans.Hosting;

namespace DigitalBrain.Postgres;

public sealed class PostgresModule() : Module<PostgresModuleOptions>(new ModuleId("postgres"))
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Postgres.IPostgresServer).Assembly;

    public static IntegrationSchema Integration => IntegrationSchema.For("postgres", "Postgres").RequiresSecret("ConnectionString").OffersSecret("AdminConnection");
    public static ConnectorDefinition Connector => new("postgres", "Postgres", ["connection-string"], ["postgres"]) { Neurons = [typeof(IPostgresServer), typeof(ITable)], Signals = [typeof(TableDefined), typeof(RowUpserted), typeof(RowDeleted)] };

    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public override void Configure(ISiloBuilder silo) => silo.AddPostgres();
}
