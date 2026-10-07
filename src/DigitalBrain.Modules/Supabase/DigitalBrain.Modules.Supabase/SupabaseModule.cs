using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Supabase;

// Supabase is a deployment backing store. A person's Supabase database is a postgres connection.
public sealed class SupabaseModule() : Module<SupabaseModuleOptions>(new ModuleId("supabase"))
{
    public static IntegrationSchema Integration => IntegrationSchema.For("supabase", "Supabase").RequiresSecret("ConnectionString");

    public const string ConnectionName = "supabase";
    public const string ProviderName = "Npgsql";

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
        services.AddSingleton<IIntegrationStartup>(provider => provider.GetRequiredService<SupabaseBackingStore>());
        services.AddHealthChecks().AddCheck<SupabaseHealthCheck>("supabase", tags: ["ready"]);
    }
}
