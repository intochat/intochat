using System.Security.Claims;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Google;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Auth.Http;

internal static class GoogleEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/google/start", StartAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
        endpoints.MapGet("/auth/google/complete", CompleteAsync).AllowAnonymous();
        // This metadata lets the remote handler receive the callback through the anonymous account-session boundary.
        endpoints.MapGet("/auth/google/callback", () => Results.Redirect("/?auth_error=google")).AllowAnonymous();
    }

    private static IResult StartAsync(HttpRequest request, AuthModuleOptions options, IOptionsMonitor<GoogleOptions> google)
    {
        if (google.Get(GoogleWiring.Scheme).ClientId == GoogleWiring.Unconfigured) { return Results.NotFound(); }
        var returnUrl = request.Query["returnUrl"].FirstOrDefault() ?? "/";
        if (!new ReturnUrlValidator(options.AllowedReturnOrigins).IsAllowed(returnUrl)) { return AuthProblems.BadRequest(AuthProblems.RequestInvalid); }
        var properties = new AuthenticationProperties { RedirectUri = "/auth/google/complete" };
        properties.Items["returnUrl"] = returnUrl;
        return Results.Challenge(properties, [GoogleWiring.Scheme]);
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, AuthModuleOptions options, AccountProvisioner provisioner,
        SessionStarter sessions, LoginService login, AuthAudit audit, ExternalTicketIssuer tickets)
    {
        var external = await http.AuthenticateAsync(GoogleWiring.ExternalScheme);
        await http.SignOutAsync(GoogleWiring.ExternalScheme);
        if (!external.Succeeded || !string.Equals(external.Principal?.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase)
            || external.Principal is not { } principal
            || EmailNormalizer.Normalize(principal.FindFirst(ClaimTypes.Email)?.Value) is not { } email
            || external.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { Length: > 0 } sub)
        {
            return Failed();
        }
        var returnUrl = external.Properties?.Items.TryGetValue("returnUrl", out var destination) == true ? destination ?? "/" : "/";
        if (!new ReturnUrlValidator(options.AllowedReturnOrigins).IsAllowed(returnUrl)) { return Failed(); }
        var member = await provisioner.CompleteGoogleSignUpAsync(email, SignUpEndpoints.DisplayName(external.Principal.FindFirst(ClaimTypes.Name)?.Value, email), sub,
            SignUpEndpoints.Language(http.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',')[0].Split('-')[0]));
        if (member is null) { return Failed(); }
        await audit.RecordAsync(member.PrincipalId, AuthAuditKind.GoogleLinked, "success", http);
        var challenge = await login.ChallengeExternalAsync(email);
        if (challenge is not null)
        {
            if (challenge.CodeId is null) { return Failed(); }
            return Results.Redirect(QueryHelpers.AddQueryString("/auth/login", new Dictionary<string, string?>
            {
                ["challengeId"] = challenge.CodeId,
                ["returnUrl"] = returnUrl,
            }));
        }
        await sessions.StartAsync(http, member, "google", http.RequestAborted);
        return Results.Redirect(await tickets.RedirectFor(returnUrl, email, external.Principal.FindFirst(ClaimTypes.Name)?.Value, "google") ?? returnUrl);
    }

    private static IResult Failed() => Results.Redirect("/?auth_error=google");
}

