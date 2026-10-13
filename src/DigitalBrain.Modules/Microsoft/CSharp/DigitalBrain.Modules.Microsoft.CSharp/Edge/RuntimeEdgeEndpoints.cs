using System.Diagnostics;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Microsoft.CSharp.Edge;

// The three routes a sandbox session speaks. The caller is the session, not a brain member, so the
// group validates the brain id and leaves authentication to the session token.
internal static class RuntimeEdgeEndpoints
{
    public static void MapRuntimeEdge(this IEndpointRouteBuilder endpoints)
    {
        var edge = BrainRoutes.SessionGroup(endpoints).ExcludeFromDescription();
        edge.MapPost(RuntimeProtocol.Invoke, async Task<IResult> (HttpContext http, string brainId, RuntimeInvocation invocation, RuntimeEdge runtime, CancellationToken cancellationToken) =>
        {
            try { return await runtime.InvokeAsync(brainId, Bearer(http), invocation, cancellationToken) is { } value ? Results.Json(value) : Results.NoContent(); }
            catch (Exception error) { return Refusal(http, error); }
        });
        edge.MapPost(RuntimeProtocol.Report, async Task<IResult> (HttpContext http, string brainId, RuntimeReport report, RuntimeEdge runtime) =>
        {
            if (report.Heartbeat) { Activity.Current?.SetTag(RuntimeProtocol.ReportKindTag, RuntimeProtocol.HeartbeatReport); }
            try { return await runtime.ReportAsync(brainId, Bearer(http), report); }
            catch (Exception error) { return Refusal(http, error); }
        });
        edge.MapGet(RuntimeProtocol.Stream, async Task<IResult> (HttpContext http, string brainId, RuntimeEdge runtime, CancellationToken cancellationToken) =>
        {
            try { return TypedResults.ServerSentEvents(await runtime.OpenStreamAsync(brainId, Bearer(http), cancellationToken)); }
            catch (Exception error) { return Refusal(http, error); }
        });
    }

    private static string? Bearer(HttpContext http)
        => http.Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;

    private static IResult Refusal(HttpContext http, Exception error)
    {
        if (error is RevisionConflictException conflict)
        {
            http.Response.Headers[RuntimeProtocol.RefusalHeader] = RevisionConflictException.Code;
            return Results.Json(new { message = conflict.Message, currentRevision = conflict.CurrentRevision }, statusCode: StatusCodes.Status409Conflict);
        }
        if (error is ServiceUnavailableException)
        {
            http.Response.Headers[RuntimeProtocol.RefusalHeader] = error.GetType().Name;
            return Results.Text(error.Message, statusCode: 503);
        }
        return error switch
        {
            UnauthorizedAccessException => Results.Text(error.Message, statusCode: StatusCodes.Status401Unauthorized),
            ArgumentException or NotSupportedException or System.Text.Json.JsonException => Results.Text(error.Message, statusCode: StatusCodes.Status400BadRequest),
            _ => Results.Text(error.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
        };
    }
}
