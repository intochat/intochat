using DigitalBrain;
using DigitalBrain.Kernel;
using Orleans.Hosting;

namespace DigitalBrain.Postgres;

public sealed class PostgresModule() : Module<PostgresModuleOptions>(new ModuleId("postgres"))
{
    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public override void Configure(ISiloBuilder silo) => silo.AddPostgres();
}
