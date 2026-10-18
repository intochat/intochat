using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Postgres.Aspire.Hosting;

public sealed class PostgresModuleHosting : ModuleHosting<IntoChat.OS.Postgres.PostgresModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("postgres").GetModuleOptions<PostgresModuleOptions>("postgres");
        var module = new IntoChatOSModuleBuilder<IntoChat.OS.Postgres.Aspire.Hosting.PostgresModuleHosting>(brain);
        if (options.Hosting.Enabled) { module.WithPostgres(options.ConnectionName, options.Hosting); }
        else { module.WithExternalConnection(options.ConnectionName); }
    }
}
