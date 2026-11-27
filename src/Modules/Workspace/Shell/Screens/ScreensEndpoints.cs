using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;

namespace IntoChat.OS.Modules.Flutter.Screens;

internal static class ScreensEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRouteGroups.Brain(endpoints, "/screens").AddEndpointFilter(ModuleRouteGuard.Guard);

        group.MapGet("", async (INeuronProxies brain, CancellationToken ct) =>
        {
            var dir = brain.Get<IScreenDirectory>(IScreenDirectory.Key(BrainScope.CurrentId()));
            return Results.Ok(await dir.List().WaitAsync(ct));
        });

        group.MapGet("/{appId}", async (string appId, string? instanceId, INeuronProxies brain, CancellationToken ct) =>
        {
            var dir = brain.Get<IScreenDirectory>(IScreenDirectory.Key(BrainScope.CurrentId()));
            var screen = await dir.Read(appId, instanceId ?? "main").WaitAsync(ct);
            return screen is null ? Results.NotFound() : Results.Ok(screen);
        });

        group.MapPost("/{appId}/act", async (string appId, ScreenActRequest input, INeuronProxies brain, CancellationToken ct) =>
        {
            var dir = brain.Get<IScreenDirectory>(IScreenDirectory.Key(BrainScope.CurrentId()));
            var result = await dir.Act(appId, input.Key, input.Value, input.Generation, input.InstanceId ?? "main").WaitAsync(ct);
            return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
        });
    }

    internal sealed record ScreenActRequest(string Key, string? Value = null, long? Generation = null, string? InstanceId = null);
}
