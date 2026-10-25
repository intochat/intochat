using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.ClickHouse.Aspire.Hosting;

public sealed class ClickHouseModuleHosting : ModuleHosting<IntoChat.OS.Modules.ClickHouse.ClickHouseModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        var options = brain.GetModuleConfiguration("clickhouse").GetModuleOptions<ClickHouseModuleOptions>("clickhouse");
        if (options.Hosting.Enabled)
        {
            new IntoChatOSModuleBuilder<IntoChat.OS.Modules.ClickHouse.Aspire.Hosting.ClickHouseModuleHosting>(brain).WithClickHouse(o =>
            {
                o.DatabaseName = options.DatabaseName;
                o.PersistentStorage = options.Hosting.PersistentStorage;
                o.AlwaysRunInitScripts = options.Hosting.AlwaysRunInitScripts;
                foreach (var seed in options.Hosting.Seeds) { o.WithSeed(seed); }
            });
        }
    }
}
