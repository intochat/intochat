using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Identity.Configuration;
using IntoChat.OS.Authority.Identity.Directory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

public static class IdentityHosting
{
    public static void AddIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // The platform endpoints resolve Authority grains through it; a web host that brings its own grain factory gets it here.
        services.TryAddSingleton<AuthorityNeurons>();
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName)
            .Validate(options => options.Posture is not null,
                $"Configure {AuthOptions.PostureKey} (Open or Secured) before starting the host.")
            .Validate(options => string.IsNullOrEmpty(options.Username) == string.IsNullOrEmpty(options.Password),
                "A Basic bootstrap credential needs both IntoChatOS:Auth:Username and IntoChatOS:Auth:Password, or neither.")
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
            });
        services.AddKernelCors();
        services.TryAddSingleton(LocalBrainAccess);
    }

    // Brain access from the directory under the configured posture. A hosted deployment wraps the directory instead.
    internal static IBrainAccess LocalBrainAccess(IServiceProvider provider)
    {
        var directory = new DirectoryBrainAccess(provider.GetRequiredService<IntoChat.OS.Framework.Platform.Identity.IIdentityClient>());
        return provider.GetRequiredService<IOptions<AuthOptions>>().Value.Posture == IdentityPosture.Open
            ? new OpenOwnerBrainAccess(directory) : directory;
    }

    public static void UsePlatformHttp(this WebApplication app)
    {
        app.UseKernelCors();
        app.UseModuleHttpSurfaces();
        app.UseAuthentication();
        foreach (var extension in app.Services.GetServices<IAuthorityHttpExtension>()) { extension.UseAfterAuthentication(app); }
        app.UseAccountSession();
        // The shell targets web and the front proxies HTTP/1.1: every gRPC route also answers gRPC-Web.
        app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
    }
}
