using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flame;

// The shell's HTTP edge to Flame under /brains/{brainId}/ui. A scene's {name} is its full key, URL-encoded.
// The neurons decide access.
internal static class FlameEndpoints
{
    internal sealed record ReportBody(string NodeId, string Kind, double X, double Y, string? Payload);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var ui = BrainRoutes.Group(endpoints, "/ui");
        ui.MapGet("/scenes/{name}", async (string name, [global::Microsoft.AspNetCore.Mvc.FromServices] IntoChat.OS.Runtime.IPlainNeurons plain, CancellationToken ct) =>
            Results.Ok(await Scene(plain, name).Read().WaitAsync(ct)));
        ui.MapPost("/scenes/{name}/report", async (string name, ReportBody body, [global::Microsoft.AspNetCore.Mvc.FromServices] IntoChat.OS.Runtime.IPlainNeurons plain, CancellationToken ct) =>
        {
            await Scene(plain, name).Report(body.NodeId, body.Kind, body.X, body.Y, body.Payload).WaitAsync(ct);
            return Results.Ok();
        });
    }

    // ASP.NET keeps %2F encoded in a route value (decoding it would change the path's segments), so the
    // scene key is unescaped here.
    // The scene is a plain neuron (06-06a), reached by its os key "{brain}/{key}".
    private static IScene Scene(IntoChat.OS.Runtime.IPlainNeurons plain, string name) => plain.Get<IScene>(Uri.UnescapeDataString(name));
}
