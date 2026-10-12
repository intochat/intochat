using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Stage;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Flame;

// The shell's HTTP edge to Flame under /brains/{brainId}/ui. A scene's {name} is its full key, URL-encoded;
// the stage is always the caller's own. The neurons decide access.
internal static class FlameEndpoints
{
    internal sealed record FireBody(string Event, string? NodeId);
    internal sealed record ReportBody(string NodeId, string Kind, double X, double Y, string? Payload);
    internal sealed record GoBody(string SceneKey, string? DockSurfaceKey);
    internal sealed record HomeBody(string SceneKey);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var ui = BrainRoutes.Group(endpoints, "/ui");
        ui.MapGet("/scenes/{name}", async (string name, IGrainFactory grains, CancellationToken ct) =>
            Results.Ok(await Scene(grains, name).Read().WaitAsync(ct)));
        ui.MapPost("/scenes/{name}/fire", async (string name, FireBody body, IGrainFactory grains, CancellationToken ct) =>
        {
            await Scene(grains, name).Fire(body.Event, body.NodeId).WaitAsync(ct);
            return Results.Ok();
        });
        ui.MapPost("/scenes/{name}/report", async (string name, ReportBody body, IGrainFactory grains, CancellationToken ct) =>
        {
            await Scene(grains, name).Report(body.NodeId, body.Kind, body.X, body.Y, body.Payload).WaitAsync(ct);
            return Results.Ok();
        });
        ui.MapGet("/stage", async (IGrainFactory grains, CancellationToken ct) =>
            Results.Ok(await (await Stage(grains)).Read().WaitAsync(ct)));
        ui.MapPost("/stage/go", async (GoBody body, IGrainFactory grains, CancellationToken ct) =>
        {
            await (await Stage(grains)).Go(body.SceneKey, body.DockSurfaceKey).WaitAsync(ct);
            return Results.Ok();
        });
        ui.MapPost("/stage/back", async (IGrainFactory grains, CancellationToken ct) =>
        {
            await (await Stage(grains)).Back().WaitAsync(ct);
            return Results.Ok();
        });
        ui.MapPost("/stage/home", async (IGrainFactory grains, CancellationToken ct) =>
        {
            await (await Stage(grains)).Home().WaitAsync(ct);
            return Results.Ok();
        });
        ui.MapPost("/stage/home/set", async (HomeBody body, IGrainFactory grains, CancellationToken ct) =>
        {
            await (await Stage(grains)).SetHome(body.SceneKey).WaitAsync(ct);
            return Results.Ok();
        });
    }

    // ASP.NET keeps %2F encoded in a route value (decoding it would change the path's segments), and a
    // catch-all cannot precede /fire, so the scene key is unescaped here.
    private static IScene Scene(IGrainFactory grains, string name) => grains.GetGrain<IScene>(Uri.UnescapeDataString(name));

    // The brain owner's shell uses the {brain}/stage/owner alias, the only key a synapse can address,
    // so both drive one stage; everyone else gets their scope digest.
    private static async Task<IStage> Stage(IGrainFactory grains)
    {
        var caller = CallerContextStamper.Require();
        return grains.GetGrain<IStage>(await StageNeuron.KeyFor(grains, caller));
    }
}
