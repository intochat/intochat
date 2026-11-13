using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Auth.Google;

internal static class GoogleWiring
{
    public const string ExternalScheme = "auth.external";
    public const string Scheme = "Google";
    internal const string Unconfigured = "unconfigured";

    public static void Register(IServiceCollection services)
    {
        services.AddAuthentication()
            .AddCookie(ExternalScheme, options =>
            {
                options.Cookie.Name = ExternalScheme;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddGoogle(options =>
            {
                options.SignInScheme = ExternalScheme;
                options.CallbackPath = "/auth/google/callback";
                options.SaveTokens = false;
                options.Scope.Add("openid");
                options.ClaimActions.MapJsonKey("email_verified", "email_verified");
                options.ClaimActions.MapJsonKey("email_verified", "verified_email");
                options.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect("/?auth_error=google");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        services.AddSingleton<IPostConfigureOptions<GoogleOptions>, GoogleConfiguration>();
    }

    // Read the deployment's Google credentials once, off the neuron scheduler, when the options are first built.
    private sealed class GoogleConfiguration(AuthSecrets secrets) : IPostConfigureOptions<GoogleOptions>
    {
        public void PostConfigure(string? name, GoogleOptions options)
        {
            if (name != Scheme) { return; }
            var (id, secret) = Task.Run(async () => (await secrets.Named(AuthSecrets.GoogleClientId), await secrets.Named(AuthSecrets.GoogleClientSecret))).GetAwaiter().GetResult();
            options.ClientId = id ?? Unconfigured;
            options.ClientSecret = secret ?? Unconfigured;
        }
    }
}
