using IntoChat.OS.Runtime;
using DigitalBrain.Spi;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Apps;

internal static class CompiledAppDevelopmentEndpoints
{
    internal sealed record BeginBody(string Name, string Project, Guid OperationId, long ExpectedRevision, bool TrustProject);
    internal sealed record LeaseBody(string Token, string Project);
    internal sealed record ReloadBody(string Name, CompiledAppReload Reload);
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var routes = BrainRoutes.Group(endpoints, "/apps/packages/development").AddEndpointFilter(ModuleRouteGuard.Guard);
        routes.MapPost("/begin", (BeginBody body, [FromServices] CompiledAppDevelopment development, CancellationToken ct)
            => AppsEndpoints.Respond(async () => Results.Ok(await development.Begin(body.Name, body.Project, body.OperationId, body.ExpectedRevision, body.TrustProject, ct))));
        routes.MapPost("/renew", (LeaseBody body, [FromServices] CompiledAppDevelopment development, CancellationToken ct)
            => AppsEndpoints.Respond(async () => Results.Ok(await development.Renew(body.Token, body.Project, ct))));
        routes.MapPost("/revoke", (LeaseBody body, [FromServices] CompiledAppDevelopment development, CancellationToken ct)
            => AppsEndpoints.Respond(async () => { await development.Revoke(body.Token, body.Project, ct); return Results.NoContent(); }));
        routes.MapPost("/reload", (ReloadBody body, [FromServices] CompiledAppDevelopment development,
            [FromServices] ICompiledAppAssignment assignment, INeuronProxies brain, CancellationToken ct)
            => AppsEndpoints.Respond(async () =>
        {
            var snapshot = await brain.Get<IApp>(IApp.Key(BrainScope.CurrentId(), body.Name)).Read().WaitAsync(ct);
            var authorized = await development.Authorize(body.Reload, body.Name, snapshot.ProgramRevision, snapshot.Connections, ct);
            return Results.Ok(AppsEndpoints.View(await assignment.Assign(authorized, ct)));
        }));
    }
}
