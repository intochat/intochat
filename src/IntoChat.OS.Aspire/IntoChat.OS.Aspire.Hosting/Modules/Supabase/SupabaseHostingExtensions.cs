using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Supabase.Aspire.Hosting;

public static class SupabaseHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Supabase.Aspire.Hosting.SupabaseModuleHosting> WithSupabase(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Supabase.Aspire.Hosting.SupabaseModuleHosting> module,
        Action<SupabaseHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var options = new SupabaseHostingOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ParameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionName);
        State(module).Enable(options);
        return module;
    }

    private static SupabaseHostingState State(IntoChatOSModuleBuilder<IntoChat.OS.Modules.Supabase.Aspire.Hosting.SupabaseModuleHosting> module)
    {
        var state = module.GetOrAddProjection(brain => new SupabaseHostingState(brain, module.Resource));

        return state;
    }

    private sealed class SupabaseHostingState(
        IntoChatOSBuilder brain,
        IResourceBuilder<IntoChatOSModuleResource> module) : IntoChatOSModuleProjection
    {
        private IResourceBuilder<ParameterResource>? _connection;
        private SupabaseHostingOptions _options = new();

        internal void Enable(SupabaseHostingOptions options)
        {
            if (_connection is not null)
            {
                throw new InvalidOperationException("Configure Supabase hosting before projecting it to an application.");
            }

            _options = options;
            _connection = brain.ApplicationBuilder
                .AddParameter(brain.ResourceName("supabase-connection"), () => HostConfiguration.Parameter(brain.ApplicationBuilder.Configuration, "supabase-connection")
                    ?? HostConfiguration.IntegrationValue(brain.ApplicationBuilder.Configuration, SupabaseModule.Integration, "ConnectionString")
                    ?? throw new InvalidOperationException($"Connection string '{_options.ParameterName}' is required."), secret: true)
                .WithParentRelationship(module);
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_connection is null)
            {
                return;
            }

            builder.WithEnvironment(DigitalBrain.Modules.Configuration.ModuleConfiguration.EnvironmentName(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("supabase"), nameof(SupabaseModuleOptions.UseBackingStore)), "true")
                .WithEnvironment(DigitalBrain.Modules.Configuration.ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString")), _connection);
        }
    }
}
