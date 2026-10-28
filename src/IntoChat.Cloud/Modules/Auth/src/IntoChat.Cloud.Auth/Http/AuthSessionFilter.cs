using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Auth.Http;

// Refuses a session cookie the sessions table no longer holds live (revoked, expired or never issued by Auth) on every Auth
// route. The same check backs identity@1 Validate, which the host's cookie validation calls for the rest of the surface.
internal sealed class AuthSessionFilter(AuthCalls calls, AuthHashing hashing) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated == true && http.User.FindFirst(SessionStarter.SidClaim)?.Value is { Length: > 0 } sid)
        {
            var principal = http.User.FindFirst(DigitalBrain.Capabilities.Identity.SessionClaims.Principal)?.Value;
            if (principal is null || !await calls.Sessions(principal, sessions => sessions.Check(hashing.SidHash(sid))))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
