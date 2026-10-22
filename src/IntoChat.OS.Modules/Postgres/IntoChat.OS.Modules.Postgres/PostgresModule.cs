using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Postgres.Tables.Signals;
using Orleans.Hosting;

namespace IntoChat.OS.Postgres;

public sealed class PostgresModule() : Module<PostgresModuleOptions>(new ModuleId("postgres"))
{
    public override bool RequiresContainer => true;

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Postgres.IPostgresServer).Assembly;

    public static IntegrationSchema Integration => IntegrationSchema.For("postgres", "Postgres").RequiresSecret("ConnectionString").OffersSecret("AdminConnection");
    public static ConnectorDefinition Connector => new("postgres", "Postgres", ["connection-string"], ["postgres"]) { Neurons = [typeof(IPostgresServer), typeof(ITable)], Signals = [typeof(TableDefined), typeof(RowUpserted), typeof(RowDeleted)] };

    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public override void Configure(ISiloBuilder silo) => silo.AddPostgres();
}
