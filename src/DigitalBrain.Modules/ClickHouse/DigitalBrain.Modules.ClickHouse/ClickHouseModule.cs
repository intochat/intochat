using ClickHouse.Driver;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.ClickHouse;

public sealed class ClickHouseModule() : Module<ClickHouseModuleOptions>(new ModuleId("clickhouse"))
{
    public static IntegrationSchema Integration => IntegrationSchema.For("clickhouse", "ClickHouse").RequiresSecret("ConnectionString");
    public static ConnectorDefinition Connector => new("clickhouse", "ClickHouse", ["connection-string"], ["clickhouse"]) { Neurons = [typeof(DigitalBrain.ClickHouse.Query.IClickHouse)], Signals = [] };

    public const string DriverProviderName = "ClickHouse";

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ClickHouseModuleOptions>()
            .Configure<IConfiguration>(static (options, configuration) => configuration.PopulateModuleOptions("clickhouse", options))
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
