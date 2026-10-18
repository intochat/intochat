using DigitalBrain.Framework;
using DigitalBrain.Authority.Identity.Configuration;
using DigitalBrain.Authority.Identity.Directory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Authority.Identity;

public static class IdentityHosting
{
    public static void AddIdentity(this IServiceCollection services)
    {
        services.AddOptions<Hosting.HostedRuntimeOptions>().BindConfiguration(Hosting.HostedRuntimeOptions.SectionName)
            .Validate(options => string.IsNullOrEmpty(options.RuntimeId) && string.IsNullOrEmpty(options.AccountId) &&
                string.IsNullOrEmpty(options.BrainId) && string.IsNullOrEmpty(options.ControllerCertificateSha256) ||
                options.IsConfigured && Path.IsPathFullyQualified(options.StateDirectory),
                "Hosted runtime requires a complete scope, pinned controller certificate and durable state directory.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Hosting.RuntimeMachineAuthentication>();
        services.TryAddSingleton<Hosting.HostedWorkspaceStore>();
        services.AddOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>()
            .Configure<IOptions<Hosting.HostedRuntimeOptions>>((server, hosting) =>
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
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName)
            .Validate(options => options.Posture is not null,
                $"Configure {AuthOptions.PostureKey} (Open or Secured) before starting the host.")
            .Validate(options => string.IsNullOrEmpty(options.Username) == string.IsNullOrEmpty(options.Password),
                "A Basic bootstrap credential needs both DigitalBrain:Auth:Username and DigitalBrain:Auth:Password, or neither.")
            .ValidateOnStart();
        services.AddOptions<IdentityHostOptions>().BindConfiguration(IdentityHostOptions.SectionName)
            .Validate(options => options.Validate(), "Identity host names must not be empty.")
            .ValidateOnStart();
        services.AddDataProtection();
        services.AddOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>()
            .Configure<IOptions<IdentityHostOptions>>((protection, host) => protection.ApplicationDiscriminator = host.Value.ProtectionApplicationName);
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentityHostOptions>>((cookie, host) =>
            {
                cookie.Cookie.Name = host.Value.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                cookie.Events.OnValidatePrincipal = Hosting.HostedWorkspaceEndpoints.ValidateCookieAsync;
            });
        services.AddKernelCors();
        services.TryAddSingleton<IBrainAccess>(provider =>
        {
            var directory = new DirectoryBrainAccess(provider.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>());
            var hosting = provider.GetRequiredService<IOptions<Hosting.HostedRuntimeOptions>>().Value;
            if (hosting.IsConfigured) { return new Hosting.HostedBrainAccess(directory, hosting); }
            return provider.GetRequiredService<IOptions<AuthOptions>>().Value.Posture == IdentityPosture.Open
                ? new OpenOwnerBrainAccess(directory) : directory;
        });
    }

    public static void UsePlatformHttp(this WebApplication app)
    {
        app.UseKernelCors();
        app.UseModuleHttpSurfaces();
        app.UseAuthentication();
        Hosting.HostedWorkspaceEndpoints.UseScope(app);
        app.UseAccountSession();
        // The shell targets web and the front proxies HTTP/1.1: every gRPC route also answers gRPC-Web.
        app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
    }
}
