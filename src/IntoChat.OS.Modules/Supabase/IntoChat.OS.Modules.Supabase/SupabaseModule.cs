using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Supabase;

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
            .Validate(static options => string.Equals(options.Provider, ProviderName, StringComparison.OrdinalIgnoreCase),
                "Supabase requires the Npgsql provider.");
        services.TryAddSingleton<SupabaseBackingStore>();
        services.TryAddSingleton<ISupabaseBackingStore>(provider => provider.GetRequiredService<SupabaseBackingStore>());
        services.AddSingleton<IIntegrationStartup>(provider => provider.GetRequiredService<SupabaseBackingStore>());
        services.AddHealthChecks().AddCheck<SupabaseHealthCheck>("supabase", tags: ["ready"]);
    }
}
