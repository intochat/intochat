using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Loopback-safe HTTP &amp; SSE endpoints for live workspace artifacts shared
/// between Ino (MCP tools) and interactive WASM studios.
/// </summary>
internal static class ArtifactEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/artifacts/meta", static (string path, HttpContext ctx, ArtifactLiveHub hub) =>
        {
            if (!IsLoopbackOrAuthenticated(ctx)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            var meta = hub.TryGetMetadata(path);
            return meta is null ? Results.NotFound(new { error = $"Artifact '{path}' not found." }) : Results.Ok(meta);
        }).AllowAnonymous().WithOrder(-100);

        endpoints.MapGet("/artifacts/bytes", static async (string path, HttpContext ctx, ArtifactLiveHub hub, CancellationToken ct) =>
        {
            if (!IsLoopbackOrAuthenticated(ctx)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            var entry = await hub.ReadAsync(path, ct).ConfigureAwait(false);
            if (entry is null) { return Results.NotFound(new { error = $"Artifact '{path}' not found." }); }
            ctx.Response.Headers.ETag = $"\"{entry.Value.Meta.Sha256}\"";
            ctx.Response.Headers["X-Artifact-Version"] = entry.Value.Meta.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.File(entry.Value.Bytes, entry.Value.Meta.MimeType);
        }).AllowAnonymous().WithOrder(-100);

        endpoints.MapPut("/artifacts/bytes", static async (string path, string? actor, HttpContext ctx, ArtifactLiveHub hub, CancellationToken ct) =>
        {
            if (!IsLoopbackOrAuthenticated(ctx)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            var meta = await hub.WriteAsync(path, ms.ToArray(), actor ?? "user", ctx.Request.ContentType, ct).ConfigureAwait(false);
            return Results.Ok(meta);
        }).AllowAnonymous().WithOrder(-100);

        endpoints.MapGet("/artifacts/stream", static async (string path, HttpContext ctx, ArtifactLiveHub hub, CancellationToken ct) =>
        {
            if (!IsLoopbackOrAuthenticated(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var (subId, reader) = hub.Subscribe(path);
            try
            {
                if (hub.TryGetMetadata(path) is { } current)
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
                hub.Unsubscribe(path, subId);
            }
        }).AllowAnonymous().WithOrder(-100);
    }

    private static bool IsLoopbackOrAuthenticated(HttpContext ctx)
        => ctx.User.Identity?.IsAuthenticated == true
           || ctx.Connection.RemoteIpAddress is null
           || IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);
}
