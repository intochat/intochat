using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Postgres.Aspire.Hosting;

public sealed class PostgresModuleHosting : ModuleHosting<DigitalBrain.Postgres.PostgresModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("postgres").GetModuleOptions<PostgresModuleOptions>("postgres");
        var module = new DigitalBrainModuleBuilder<DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting>(brain);
        if (options.Hosting.Enabled) { module.WithPostgres(options.ConnectionName, options.Hosting); }
        else { module.WithExternalConnection(options.ConnectionName); }
    }
}
