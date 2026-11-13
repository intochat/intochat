using System.Security.Claims;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Auth.Services;

// The signed-in person of a session-required route, read from the session claims the host issued.
internal static class AuthCaller
{
    public static CapabilityCaller Require(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Create(http.User);
    }

    public static CapabilityCaller Create(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true
            || user.FindFirst(SessionClaims.Principal)?.Value is not { Length: > 0 } principal)
        {
            throw new UnauthorizedAccessException("This route needs a signed-in caller.");
        }

        var account = user.FindFirst(SessionClaims.Account)?.Value is { Length: > 0 } a ? a : principal;
        var brain = user.FindFirst(SessionClaims.Brain)?.Value is { Length: > 0 } b ? b : account;
        return CapabilityCallers.Of(principal, account, brain);
    }
}
