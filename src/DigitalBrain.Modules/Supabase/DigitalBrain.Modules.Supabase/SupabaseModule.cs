using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Supabase.Windows;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orleans.Hosting;

namespace DigitalBrain.Supabase;

public sealed class SupabaseModule() : Module<SupabaseModuleOptions>(new ModuleId("supabase")), IHttpModule
{
    public static IntegrationSchema Integration => IntegrationSchema.For("supabase", "Supabase").RequiresSecret("ConnectionString");
    public static ConnectorDefinition Connector => new("supabase", "Supabase", ["connection-string"], []) { OptionalIntegrations = ["supabase"], Neurons = [typeof(ISupabase), typeof(DigitalBrain.Supabase.Tables.ISupabaseTable)], Signals = [typeof(DigitalBrain.Supabase.Tables.Signals.SupabaseTableChanged)] };

    public const string ConnectionName = "supabase";
    public const string ProviderName = "Npgsql";

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        TableEndpoints.Map(endpoints);
    }

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<SupabaseModuleOptions>()
            .Configure<IConfiguration>(static (options, configuration) => configuration.PopulateModuleOptions("supabase", options))
            .Validate(static options => string.Equals(options.Provider, ProviderName, StringComparison.OrdinalIgnoreCase),
                "Supabase requires the Npgsql provider.");
        services.TryAddSingleton<SupabaseBackingStore>();
        services.TryAddSingleton<ISupabaseBackingStore>(provider => provider.GetRequiredService<SupabaseBackingStore>());
        // Live tables and their tools read the calling brain's own connection neuron, never the
        // deployment store; only the integration key ("default") of SupabaseNeuron serves that.
        services.TryAddSingleton<ISupabaseProvider, AmbientSupabaseSource>();
        services.TryAddSingleton<ISupabaseSourceFactory, NpgsqlSupabaseSourceFactory>();
        services.AddSingleton<IIntegrationStartup>(provider => provider.GetRequiredService<SupabaseBackingStore>());
        services.AddLiveTables();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Framework.IRegistryResourceProvider, SupabaseRegistryResources>());
        services.AddSingleton<IAgentToolFactory, LiveTableTools>();
        services.TryAddSingleton<ILiveTableSource>(services => services.GetRequiredService<ISupabaseProvider>());
        services.AddHealthChecks().AddCheck<SupabaseHealthCheck>("supabase", tags: ["ready"]);
    }

}
