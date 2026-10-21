using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Hosted;

// Container management of one hosted allocation: machine-certificate authentication, ensure, observation and the
// allocation's journal storage. It owns no identity: who signs in, and which brains they reach, is the platform's own
// rule (external sign-in and brain membership), exactly as on any other install. Everything it adds is inert until
// HostedRuntimeOptions is configured, except the options validation: a partial scope refuses to start.
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
                options.IsConfigured,
                "Hosted runtime requires a complete scope and a pinned controller certificate.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RuntimeMachineAuthentication>();
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
        services.AddSingleton<IAuthorityHttpExtension, HostedHttp>();
        return services;
    }

    private sealed class HostedAuthorityRegistration;

    // When Hosting:RuntimeId is configured, platform authority and reminders live in the allocation's journal. Kept as
    // recovery storage: every existing allocation already holds its accounts, principals, integration secrets and
    // reminders there, so moving them to the default store would need a state migration. The guard below refuses to
    // start over a configured grain-state store rather than silently replace existing authority.
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

    // Management routes only. No request pipeline of its own: an ext- principal from external sign-in is admitted to the
    // brains it is a member of, like on any install.
    private sealed class HostedHttp : IAuthorityHttpExtension
    {
        public void UseAfterAuthentication(WebApplication app) { }
        public void Map(IEndpointRouteBuilder endpoints) => HostedRuntimeEndpoints.Map(endpoints);
    }
}
