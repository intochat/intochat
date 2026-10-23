using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Flutter.Settings;

// Per-brain notification preferences, stored next to the display name and theme under {brain}/settings
// as text fields holding "on" or "off". Any member may read and change them.
internal static class NotificationPreferenceEndpoints
{
    private static readonly (string Key, bool Default)[] Keys =
    [
        ("agent-finished", true),
        ("needs-approval", true),
        ("connection-errors", true),
        ("product-updates", false),
    ];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/preferences/notifications");
        group.MapGet("", async (IIntoChatOS brain, CancellationToken ct) => Results.Ok(await Read(brain, ct)));
        group.MapPut("", Write);
    }

    // Installed apps (App callers or calls through the app proxy) cannot change a person's preferences,
    // as brain entries refuse them. The fields are text-field neurons, so this guards the HTTP path.
    internal static async Task<IResult> Write(Dictionary<string, bool>? input, IIntoChatOS brain, CancellationToken ct)
    {
        if (CallerContextStamper.TryGet(out var caller) && (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy))
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        if (input is null || input.Keys.Any(key => !Keys.Any(known => known.Key == key)))
        { return Results.BadRequest(new { error = "Unknown notification preference." }); }
        foreach (var (key, value) in input)
        {
            await brain.Get<ITextField>(Name(key)).SetValue(value ? "on" : "off").WaitAsync(ct);
        }
        return Results.Ok(await Read(brain, ct));
    }

    private static string Name(string key) => BrainScope.CurrentId() + "/settings/notifications/" + key;

    private static async Task<Dictionary<string, bool>> Read(IIntoChatOS brain, CancellationToken ct)
    {
        var result = new Dictionary<string, bool>();
        foreach (var (key, fallback) in Keys)
        {
            var state = await brain.Get<ITextField>(Name(key)).Read().WaitAsync(ct);
            result[key] = state.Value switch { "on" => true, "off" => false, _ => fallback };
        }
        return result;
    }
}
