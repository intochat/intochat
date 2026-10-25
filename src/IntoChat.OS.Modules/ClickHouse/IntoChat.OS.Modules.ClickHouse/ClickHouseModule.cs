using ClickHouse.Driver;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.ClickHouse;

public sealed class ClickHouseModule() : Module<ClickHouseModuleOptions>(new ModuleId("clickhouse"))
{
    public override bool RequiresContainer => true;

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.ClickHouse.Query.IClickHouse).Assembly;

    public static IntegrationSchema Integration => IntegrationSchema.For("clickhouse", "ClickHouse").RequiresSecret("ConnectionString");
    public static ConnectorDefinition Connector => new("clickhouse", "ClickHouse", ["connection-string"], ["clickhouse"]) { Neurons = [typeof(IntoChat.OS.Modules.ClickHouse.Query.IClickHouse)], Signals = [] };

    public const string DriverProviderName = "ClickHouse";

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ClickHouseModuleOptions>()
            .Validate(static options => string.Equals(options.Provider, DriverProviderName, StringComparison.OrdinalIgnoreCase),
                "ClickHouse requires the module option Provider=ClickHouse.");
        services.AddHttpClient(ClickHouseRegistration.HttpClientName, static client => client.Timeout = TimeSpan.FromMinutes(2));
        services.TryAddSingleton<IntegrationClickHouseProvider>();
        services.TryAddSingleton<IClickHouseProvider>(provider => provider.GetRequiredService<IntegrationClickHouseProvider>());
        services.AddSingleton<IIntegrationStartup>(provider => provider.GetRequiredService<IntegrationClickHouseProvider>());
        services.TryAddSingleton<IClickHouseSourceFactory, ClickHouseClientSourceFactory>();
        services.AddHealthChecks().AddCheck<ClickHouseHealthCheck>("clickhouse", tags: ["ready"]);
    }
}
