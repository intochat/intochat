using DigitalBrain.Launcher.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Launcher.Revisions;

public sealed record ProposeRevision(string Branch, string Summary, Dictionary<string, string> Files);

public static class LauncherEndpoints
{
    public static IEndpointRouteBuilder MapLauncher(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/revisions", (Revisions revisions) => Results.Ok(revisions.List()));
        app.MapPost("/revisions", async (ProposeRevision request, Revisions revisions, CancellationToken ct) =>
        {
            var revision = await revisions.ProposeAsync(request.Branch, request.Summary, request.Files, ct);
            return Results.Created($"/revisions/{revision.Sha}", revision);
        });
        app.MapPost("/revisions/{sha}/start", (string sha, Revisions revisions, CancellationToken ct) => Known(() => revisions.StartAsync(sha, ct)));
        app.MapPost("/revisions/{sha}/healthy", async (string sha, TimeSpan budget, Revisions revisions, CancellationToken ct) =>
        {
            try { return Results.Ok(await revisions.WaitHealthyAsync(sha, budget, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (TimeoutException error) { return Results.Json(new { reason = error.Message }, statusCode: StatusCodes.Status504GatewayTimeout); }
            catch (InvalidOperationException error) { return Results.Json(new { reason = error.Message }, statusCode: StatusCodes.Status502BadGateway); }
        });
        app.MapPost("/revisions/{sha}/promote", (string sha, Revisions revisions, CancellationToken ct) => Known(() => revisions.PromoteAsync(sha, ct)));
        app.MapDelete("/revisions/{sha}", async (string sha, Revisions revisions, CancellationToken ct) =>
        {
            try { await revisions.DiscardAsync(sha, ct); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        app.MapGet("/revisions/spans", async (string endpoint, string name, Revisions revisions, LauncherOptions options, Telemetry.IAspireTelemetry telemetry, CancellationToken ct) =>
        {
            var revision = revisions.List().Revisions.SingleOrDefault(known => known.McpEndpoint == endpoint);
            if (revision is null) { return Results.NotFound(); }
            return Results.Ok(await telemetry.CountSpans(Path.Combine(revision.Worktree, options.AppHostProject), name, ct));
        });
        return app;
    }

    private static async Task<IResult> Known(Func<Task<Revision>> action)
    {
        try { return Results.Ok(await action()); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}
