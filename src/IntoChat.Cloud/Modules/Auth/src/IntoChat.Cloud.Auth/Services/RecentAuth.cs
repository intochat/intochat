using System.Globalization;
using System.Security.Claims;

namespace IntoChat.Cloud.Auth.Services;

internal static class RecentAuth
{
    public static bool IsRecent(ClaimsPrincipal principal, TimeProvider clock, TimeSpan window) =>
        long.TryParse(principal.FindFirst(SessionStarter.AuthTimeClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds <= clock.GetUtcNow().ToUnixTimeSeconds()
        && clock.GetUtcNow().ToUnixTimeSeconds() - seconds <= window.TotalSeconds;
}
