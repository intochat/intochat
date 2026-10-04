using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using Orleans.Hosting;

namespace DigitalBrain.Postgres;

[ModuleHosting("DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting, DigitalBrain.Modules.Postgres.Aspire.Hosting")]
public sealed class PostgresModule() : Module<PostgresModuleOptions>(new ModuleId("postgres"))
{
    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public override void Configure(ISiloBuilder silo) => silo.AddPostgres();
}
