using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using Orleans.Hosting;

namespace DigitalBrain.Postgres;

[ModuleHosting("DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting, DigitalBrain.Modules.Postgres.Aspire.Hosting")]
[ModuleId("postgres")]
public sealed class PostgresModule : IModule<PostgresModuleOptions>
{
    public const string ConnectionName = "postgres";
    public const string ProviderName = "Npgsql";

    public void Configure(ISiloBuilder silo) => silo.AddPostgres();
}
