using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Hosted;

// The hosted-cloud composition over the platform. Everything it adds is inert until HostedRuntimeOptions is
// configured, except the options validation: a partial scope refuses to start.
public static class HostedAuthorityHosting
{
    public static IHostApplicationBuilder AddHostedAuthority(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHostedAuthority(builder.Configuration);
        return builder;
    }

    // Order-free against the platform: storage applies through AddBrainStorage, or at once when it already ran.
    public static IServiceCollection AddHostedAuthority(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(HostedAuthorityRegistration))) { return services; }
        services.AddSingleton<HostedAuthorityRegistration>();
        var storage = new HostedStorage();
        services.AddSingleton<IHostedStorageExtension>(storage);
        if (BrainStorageHosting.IsComposed(services)) { storage.Configure(services, configuration); }
        services.AddOptions<HostedRuntimeOptions>().BindConfiguration(HostedRuntimeOptions.SectionName)
            .Validate(options => string.IsNullOrEmpty(options.RuntimeId) && string.IsNullOrEmpty(options.AccountId) &&
                string.IsNullOrEmpty(options.BrainId) && string.IsNullOrEmpty(options.ControllerCertificateSha256) ||
                options.IsConfigured && Path.IsPathFullyQualified(options.StateDirectory),
                "Hosted runtime requires a complete scope, pinned controller certificate and durable state directory.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RuntimeMachineAuthentication>();
        services.TryAddSingleton<HostedWorkspaceStore>();
        services.AddOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>()
            .Configure<IOptions<HostedRuntimeOptions>>((server, hosting) =>
            {
                if (!hosting.Value.IsConfigured) { return; }
                server.ConfigureHttpsDefaults(https =>
                {
                    https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
                    https.ClientCertificateValidation = (certificate, _, _) =>
                        string.Equals(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
                            hosting.Value.ControllerCertificateSha256, StringComparison.OrdinalIgnoreCase);
                });
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure(cookie => cookie.Events.OnValidatePrincipal = HostedWorkspaceEndpoints.ValidateCookieAsync);
        // Registered last so it wins over the platform's local access whichever composes first.
        services.AddSingleton<IBrainAccess>(provider =>
        {
            var hosting = provider.GetRequiredService<IOptions<HostedRuntimeOptions>>().Value;
            return hosting.IsConfigured
                ? new HostedBrainAccess(BrainAccess.Directory(provider.GetRequiredService<IntoChat.OS.Framework.Platform.Identity.IIdentityClient>()), hosting)
                : BrainAccess.Local(provider);
        });
        services.AddSingleton<IAuthorityHttpExtension, HostedHttp>();
        return services;
    }

    private sealed class HostedAuthorityRegistration;

    // When Hosting:RuntimeId is configured, platform authority and reminders live in the allocation's journal.
    private sealed class HostedStorage : IHostedStorageExtension
    {
        public void Configure(IServiceCollection services, IConfiguration configuration)
        {
            if (string.IsNullOrEmpty(configuration[HostedRuntimeOptions.SectionName + ":RuntimeId"])) { return; }
            if (!string.IsNullOrWhiteSpace(configuration[IntoChat.OS.Framework.Platform.Integrations.IntegrationSchema.Key("orleans", "GrainState")]))
            {
                throw new InvalidOperationException("Hosted journal storage requires an explicit migration from the configured Orleans grain-state store; existing authority and secrets must not be replaced.");
            }
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, IntoChatOSNames.PlatformGrainStorage)).ToArray())
            { services.Remove(descriptor); }
            services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => ActivatorUtilities.CreateInstance<HostedPlatformGrainStorage>(provider));
            services.RemoveAll<IReminderTable>();
            services.AddSingleton<IReminderTable, HostedReminderTable>();
        }
    }

    private sealed class HostedHttp : IAuthorityHttpExtension
    {
        public void UseAfterAuthentication(WebApplication app) => HostedWorkspaceEndpoints.UseScope(app);
        public void Map(IEndpointRouteBuilder endpoints) => HostedRuntimeEndpoints.Map(endpoints);
    }
}
