using DigitalBrain.Modules.Options;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Postgres.Aspire.Hosting;

public sealed class PostgresModuleHosting : ModuleHosting<IntoChat.OS.Modules.Postgres.PostgresModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("postgres").GetModuleOptions<PostgresModuleOptions>("postgres");
        var module = new IntoChatOSModuleBuilder<IntoChat.OS.Modules.Postgres.Aspire.Hosting.PostgresModuleHosting>(brain);
        if (options.Hosting.Enabled) { module.WithPostgres(options.ConnectionName, options.Hosting); }
        else { module.WithExternalConnection(options.ConnectionName); }
    }
}
