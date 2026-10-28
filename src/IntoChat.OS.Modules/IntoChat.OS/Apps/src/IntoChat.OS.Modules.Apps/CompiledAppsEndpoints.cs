using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Apps;

internal static class CompiledAppsEndpoints
{
    public sealed record ReviewBody(string Digest, string Name);
    public sealed record ActivateBody(string Name, CompiledAppApproval Approval);

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var packages = BrainRoutes.Group(endpoints, "/apps/packages").AddEndpointFilter(ModuleRouteGuard.Guard);
        packages.MapPost("", (HttpContext http, [FromServices] CompiledAppPackages cache,
            [FromServices] CompiledAppInstaller installer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            _ = await installer.RequireOwner(ct);
            // These non-simple media types also prevent a cross-origin HTML form from uploading executable code.
            if (http.Request.ContentType is not ("application/octet-stream" or "application/vnd.intochat.app+zip"))
            { return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); }
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            { limit.MaxRequestBodySize = 128L * 1024 * 1024; }
            var package = await cache.Stage(http.Request.Body, ct);
            var manifest = CompiledPackageMetadata.Manifest(package);
            return Results.Ok(new
            {
                package.Digest,
                package.Descriptor.Id,
                package.Descriptor.Name,
                package.Descriptor.Version,
                requires = manifest.Requires ?? new(),
                hostAdapters = package.Descriptor.RequiredHostAdapters,
                trustMessage = CompiledAppReview.TrustMessage,
            });
        }));
        packages.MapPost("/review", (ReviewBody body, [FromServices] CompiledAppInstaller installer, CancellationToken ct)
            => AppsEndpoints.Respond(async () =>
            {
                var review = await installer.Prepare(body.Digest, body.Name, ct);
                return Results.Ok(new
                {
                    review.Token,
                    review.Digest,
                    review.Brain,
                    review.Name,
                    review.ExpectedRevision,
                    review.ReplacesExisting,
                    review.ExpiresAt,
                    review.Requires,
                    review.HostAdapters,
                    requirementsConsent = AppConsent.Of(review.Requires),
                    trustMessage = CompiledAppReview.TrustMessage,
                });
            }));
        packages.MapPost("/activate", (ActivateBody body, [FromServices] CompiledAppInstaller installer,
            [FromServices] ICompiledAppAssignment assignment, IIntoChatOS brain, CancellationToken ct)
            => AppsEndpoints.Respond(async () =>
        {
            var snapshot = await brain.Get<IApp>(IApp.Key(BrainScope.CurrentId(), body.Name)).Read().WaitAsync(ct);
            var authorized = await installer.Authorize(body.Approval, body.Name, snapshot.ProgramRevision, snapshot.Connections, ct);
            return Results.Ok(AppsEndpoints.View(await assignment.Assign(authorized, ct)));
        }));
    }
}
