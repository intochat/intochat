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
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true
            || user.FindFirst(SessionClaims.Principal)?.Value is not { Length: > 0 } principal)
        {
            throw new UnauthorizedAccessException("This route needs a signed-in caller.");
        }

        return CapabilityCallers.Of(principal, user.FindFirst(SessionClaims.Account)?.Value ?? "", user.FindFirst(SessionClaims.Brain)?.Value ?? "");
    }
}

internal static class CapabilityCallers
{
    public static CapabilityCaller Of(string principalId, string accountId, string brainId, string? appId = null, string? conversationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        return new(principalId, accountId, brainId, appId, conversationId);
    }
}
