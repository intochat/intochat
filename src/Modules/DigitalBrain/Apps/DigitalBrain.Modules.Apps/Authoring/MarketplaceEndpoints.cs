using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps;

internal sealed record CreatorRequest(string Text);
internal sealed record WriteFileRequest(string Path, string Source);
internal sealed record RunScenariosRequest(string[]? ScenarioIds = null);
internal sealed record SaveAppRequest(string? Message = null);

internal static class MarketplaceEndpoints
{
    public static void MapMarketplace(this IEndpointRouteBuilder routes)
    {
        var packages = routes.MapGroup("/packages").AddEndpointFilter(ModuleRouteGuard.Guard);
        if (routes.ServiceProvider.GetService<IScriptSandbox>()?.CanRun == true)
        { PackageEndpoints.MapAuthoring(packages); }
        packages.MapGet("/{owner}/{name}/spec", (string owner, string name, string? revision, [global::Microsoft.AspNetCore.Mvc.FromServices] MarketplaceService marketplace)
            => marketplace.Spec(PackageId.Create(owner, name), revision));
        packages.MapPost("/{owner}/{name}/verify", (string owner, string name, string? revision, [global::Microsoft.AspNetCore.Mvc.FromServices] MarketplaceService marketplace)
            => marketplace.Verify(PackageId.Create(owner, name), revision));

        var installed = BrainRoutes.Group(routes, "/packages/{owner}/{name}").AddEndpointFilter(ModuleRouteGuard.Guard);
        installed.MapGet("/invocations/{invocationId:guid}/discussion", (string owner, string name, Guid invocationId, [global::Microsoft.AspNetCore.Mvc.FromServices] MarketplaceService marketplace)
            => marketplace.Discussion(InstalledPackages.AppKey(PackageId.Create(owner, name)), invocationId));

        // Starting an app needs no name: the Author names it and it exists, installed and
        // running, under its creator's ownership.
        var creatorRoot = BrainRoutes.Group(routes, "/creator").AddEndpointFilter(ModuleRouteGuard.Guard);
        creatorRoot.MapPost("/apps", async (CreatorRequest request, [global::Microsoft.AspNetCore.Mvc.FromServices] CreatorService creatorService,
            [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain, CancellationToken ct) =>
        {
            var (package, content) = await creatorService.Start(brain, request.Text, ct);
            return Results.Ok(new { package.Owner, package.Name, Content = content });
        });

        // The Creator operates on the live app: created running, edited per behavior, allowed
        // red. Save commits to the package; publish (on PackageEndpoints) stays the only gate.
        var creator = BrainRoutes.Group(routes, "/packages/{owner}/{name}/creator").AddEndpointFilter(ModuleRouteGuard.Guard)
            // A live app is created under its author's name, and only that author drives its Creator.
            .AddEndpointFilter(async (context, next) =>
            {
                var owner = context.HttpContext.Request.RouteValues["owner"] as string;
                return CallerContextStamper.Require().PrincipalId != owner
                    ? Results.Problem("This app belongs to another person. Fork it to make your own.", statusCode: StatusCodes.Status403Forbidden)
                    : await next(context);
            });
        creator.MapGet("/content", (string owner, string name , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain)
            => App(brain, owner, name).ReadContent());
        creator.MapPost("/author", async (string owner, string name, CreatorRequest request, [global::Microsoft.AspNetCore.Mvc.FromServices] CreatorService creatorService , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain, CancellationToken ct) =>
        {
            return Results.Ok(await creatorService.Author(App(brain, owner, name), request.Text, ct));
        });
        creator.MapPost("/implement", async (string owner, string name, CreatorRequest request, [global::Microsoft.AspNetCore.Mvc.FromServices] CreatorService creatorService , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain, CancellationToken ct) =>
        {
            return Results.Ok(await creatorService.Implement(App(brain, owner, name), request.Text, ct));
        });
        creator.MapPut("/behaviors", async (string owner, string name, WriteFileRequest request , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain) =>
        {
            return Results.Ok(await App(brain, owner, name).WriteBehavior(request.Path, request.Source, Guid.NewGuid()));
        });
        creator.MapPut("/files", async (string owner, string name, WriteFileRequest request , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain) =>
        {
            return Results.Ok(await App(brain, owner, name).WriteFile(request.Path, request.Source, Guid.NewGuid()));
        });
        creator.MapPut("/document", async (string owner, string name, AppAuthoringDocument document , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain) =>
        {
            return Results.Ok(await App(brain, owner, name).SaveDocument(document, Guid.NewGuid()));
        });
        creator.MapPost("/run", async (string owner, string name, RunScenariosRequest request, [global::Microsoft.AspNetCore.Mvc.FromServices] LiveAppPackaging packaging , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain, CancellationToken ct) =>
        {
            return Results.Ok(await packaging.RunScenarios(App(brain, owner, name), PackageId.Create(owner, name), request.ScenarioIds, ct));
        });
        creator.MapPost("/save", async (string owner, string name, SaveAppRequest request, [global::Microsoft.AspNetCore.Mvc.FromServices] LiveAppPackaging packaging , [global::Microsoft.AspNetCore.Mvc.FromServices] IDigitalBrain brain) =>
        {
            return Results.Ok(await packaging.Save(App(brain, owner, name), PackageId.Create(owner, name), request.Message ?? "Save"));
        });
    }

    private static IApp App(IDigitalBrain brain, string owner, string name)
        => brain.Get<IApp>(BrainScope.CurrentId() + "/packages/" + PackageId.Create(owner, name));
}
