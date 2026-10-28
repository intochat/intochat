using DigitalBrain.Capabilities.Identity;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Auth.Services;

// The signed-in person of a session-required route, read from the session claims the host issued (SessionClaims): the one place
// Auth turns an authenticated request into principal, account and brain.
internal sealed record AuthCaller(string PrincipalId, string AccountId, string BrainId)
{
    public static AuthCaller Require(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true
            || user.FindFirst(SessionClaims.Principal)?.Value is not { Length: > 0 } principal)
        {
            throw new UnauthorizedAccessException("This route needs a signed-in caller.");
        }

        return new AuthCaller(principal, user.FindFirst(SessionClaims.Account)?.Value ?? "", user.FindFirst(SessionClaims.Brain)?.Value ?? "");
    }
}
