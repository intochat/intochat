using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using IntoChat.OS.Host.Http;

namespace IntoChat.OS.Modules.Flutter.Settings;

// Per-brain notification preferences, stored next to the display name and theme under {brain}/settings
// as text fields holding "on" or "off". Any member may read and change them.
internal static class NotificationPreferenceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = OsBrainRoutes.Group(endpoints, "/preferences/notifications");
        group.MapGet("", async (INeuronProxies brain, CancellationToken ct) => Results.Ok(await BrainPreferenceStore.ReadNotifications(brain, BrainScope.CurrentId(), ct)));
        group.MapPut("", Write);
    }

    // Installed apps (App callers or calls through the app proxy) cannot change a person's preferences,
    // as brain entries refuse them. The fields are text-field neurons, so this guards the HTTP path.
    internal static async Task<IResult> Write(HttpContext http, Dictionary<string, bool>? input, INeuronProxies brain, CancellationToken ct)
    {
        if (DigitalBrain.Runtime.Http.Surface.HttpCaller.Current(http) is not { } caller || BrainPreferenceStore.IsAppCaller(caller)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        if (input is null || input.Keys.Any(key => !BrainPreferenceStore.IsNotificationKey(key)))
        { return Results.BadRequest(new { error = "Unknown notification preference." }); }
        var scope = BrainScope.CurrentId();
        foreach (var (key, value) in input) { await BrainPreferenceStore.WriteNotification(brain, scope, key, value, ct); }
        return Results.Ok(await BrainPreferenceStore.ReadNotifications(brain, scope, ct));
    }
}
