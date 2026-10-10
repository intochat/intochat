using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

internal static class SandboxEndpoints
{
    public static void MapSandbox(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => TypedResults.Ok());
        endpoints.MapPost("/sessions/{identifier}", async (string identifier, SessionStart start, Sessions sessions, CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.StartAsync(identifier, start, cancellationToken);
                return Results.Accepted($"/sessions/{identifier}");
            }
            catch (UriFormatException error) { return Results.BadRequest(error.Message); }
            catch (InvalidOperationException error) { return Results.Conflict(error.Message); }
        });
        endpoints.MapGet("/sessions/{identifier}", (string identifier, Sessions sessions)
            => sessions.Contains(identifier) ? Results.Ok() : Results.NotFound());
        endpoints.MapPost("/sessions/{identifier}/judge", async (string identifier, JudgeRequest request, RuleJudge judge, CancellationToken cancellationToken)
            => Results.Ok(await judge.JudgeAsync(identifier, request, cancellationToken)));
        endpoints.MapPost("/sessions/{identifier}/stop", async (string identifier, Sessions sessions)
            => await sessions.StopAsync(identifier) ? Results.Ok() : Results.NotFound());
    }
}
