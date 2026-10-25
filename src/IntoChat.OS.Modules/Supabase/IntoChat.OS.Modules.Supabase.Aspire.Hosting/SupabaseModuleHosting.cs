using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Platform.Integrations;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Supabase.Aspire.Hosting;

public sealed class SupabaseModuleHosting : ModuleHosting<IntoChat.OS.Modules.Supabase.SupabaseModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        var options = brain.GetModuleConfiguration("supabase").GetModuleOptions<SupabaseModuleOptions>("supabase");
        if (!options.UseBackingStore) { return; }
        var module = new IntoChatOSModuleBuilder<IntoChat.OS.Modules.Supabase.Aspire.Hosting.SupabaseModuleHosting>(brain);
        if (options.Hosting.Kind == SupabaseHostKind.External)
        {
            module.WithSupabase(o => { o.ParameterName = options.ConnectionName; o.ConnectionName = options.ConnectionName; });
            return;
        }
        var server = brain.ApplicationBuilder.AddPostgres(brain.ResourceName("supabase-postgres")).WithParentRelationship(module.Resource);
        var database = server.AddDatabase(brain.ResourceName("supabase-database"), options.ConnectionName);
        brain.AddProjection(new PostgresProjection(database));
    }

    private sealed class PostgresProjection(IResourceBuilder<PostgresDatabaseResource> database)
        : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(IntoChat.OS.Sdk.Configuration.IntoChatOSConfiguration.EnvironmentName(IntoChat.OS.Sdk.Configuration.IntoChatOSConfiguration.ModuleOptionsSection("supabase"), nameof(SupabaseModuleOptions.UseBackingStore)), "true")
                .WithEnvironment(IntoChat.OS.Sdk.Configuration.IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString")), database.Resource.ConnectionStringExpression)
                .WithAnnotation(new WaitAnnotation(database.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
    }
}
