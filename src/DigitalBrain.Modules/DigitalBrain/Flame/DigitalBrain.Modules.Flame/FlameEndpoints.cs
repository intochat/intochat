using DigitalBrain.Flame.Scene;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Flame;

// The shell's HTTP edge to Flame under /brains/{brainId}/ui. A scene's {name} is its full key, URL-encoded.
// The neurons decide access.
internal static class FlameEndpoints
{
    internal sealed record ReportBody(string NodeId, string Kind, double X, double Y, string? Payload);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var ui = BrainRoutes.Group(endpoints, "/ui");
        ui.MapGet("/scenes/{name}", async (string name, IGrainFactory grains, CancellationToken ct) =>
            Results.Ok(await Scene(grains, name).Read().WaitAsync(ct)));
        ui.MapPost("/scenes/{name}/report", async (string name, ReportBody body, IGrainFactory grains, CancellationToken ct) =>
        {
            await Scene(grains, name).Report(body.NodeId, body.Kind, body.X, body.Y, body.Payload).WaitAsync(ct);
            return Results.Ok();
        });
    }

    // ASP.NET keeps %2F encoded in a route value (decoding it would change the path's segments), so the
    // scene key is unescaped here.
    private static IScene Scene(IGrainFactory grains, string name) => grains.GetGrain<IScene>(Uri.UnescapeDataString(name));
}
