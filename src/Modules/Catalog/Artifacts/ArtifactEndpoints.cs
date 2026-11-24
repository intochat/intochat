using System.Text.Json;
using DigitalBrain.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// HTTP &amp; SSE endpoints for live workspace artifacts shared between Ino (MCP tools) and interactive WASM studios.
/// Every read and write is for the brain of the stamped caller, and only a person's session reaches them: a studio page
/// calls with the session cookie its WebView was entered with, or the presence proof of the person at this machine.
/// The routes carry no brain: the session names it, so one person's artifacts are never another's.
/// </summary>
internal static class ArtifactEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/artifacts/meta", static (string path, HttpContext ctx, ArtifactLiveHub hub) =>
        {
            if (Brain(ctx) is not { } brain) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            var meta = hub.TryGetMetadata(path, brain);
            return meta is null ? Results.NotFound(new { error = $"Artifact '{path}' not found." }) : Results.Ok(meta);
        }).WithOrder(-100);

        endpoints.MapGet("/artifacts/bytes", static async (string path, HttpContext ctx, ArtifactLiveHub hub, CancellationToken ct) =>
        {
            if (Brain(ctx) is not { } brain) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            var entry = await hub.ReadAsync(path, ct, brain).ConfigureAwait(false);
            if (entry is null) { return Results.NotFound(new { error = $"Artifact '{path}' not found." }); }
            ctx.Response.Headers.ETag = $"\"{entry.Value.Meta.Sha256}\"";
            ctx.Response.Headers["X-Artifact-Version"] = entry.Value.Meta.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.File(entry.Value.Bytes, entry.Value.Meta.MimeType);
        }).WithOrder(-100);

        // An actor in the query is ignored: who wrote is the stamped caller, a person at a studio.
        endpoints.MapPut("/artifacts/bytes", static async (string path, HttpContext ctx, ArtifactLiveHub hub, CancellationToken ct) =>
        {
            if (Brain(ctx) is not { } brain) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            var meta = await hub.WriteAsync(path, ms.ToArray(), "user", ctx.Request.ContentType, ct, brain).ConfigureAwait(false);
            return Results.Ok(meta);
        }).WithOrder(-100);

        endpoints.MapGet("/artifacts/stream", static async (string path, HttpContext ctx, ArtifactLiveHub hub, CancellationToken ct) =>
        {
            if (Brain(ctx) is not { } brain)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var (subId, reader) = hub.Subscribe(path, brain);
            try
            {
                if (hub.TryGetMetadata(path, brain) is { } current)
                {
                    var initialJson = JsonSerializer.Serialize(
                        new ArtifactChanged(current.Path, current.Version, current.Sha256, current.UpdatedBy, current.UpdatedAt),
                        JsonOptions);
                    await ctx.Response.WriteAsync($"event: artifact\ndata: {initialJson}\n\n", ct).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }

                await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(evt, JsonOptions);
                    await ctx.Response.WriteAsync($"event: artifact\ndata: {json}\n\n", ct).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                hub.Unsubscribe(path, subId, brain);
            }
        }).WithOrder(-100);
    }

    // The brain of the caller the session middleware stamped, when that caller is a person. The routes are not anonymous
    // any more, so a request with no session is answered 401 before it gets here; an app, an agent or a guest is refused.
    private static string? Brain(HttpContext ctx)
        => ctx.RequestServices.GetService<ICallerAmbient>()?.Current is { Actor: Actor.Person } caller ? caller.Brain.Value : null;
}
