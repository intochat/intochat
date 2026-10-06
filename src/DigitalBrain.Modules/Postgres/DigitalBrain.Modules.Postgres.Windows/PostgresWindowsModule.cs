using DigitalBrain.Framework;
using Orleans.Hosting;

namespace DigitalBrain.Postgres.Windows;

public sealed class PostgresWindowsModule() : Module(new ModuleId("postgres-windows"))
{
    public override void Configure(ISiloBuilder silo) => silo.AddPostgresWindows();
}
