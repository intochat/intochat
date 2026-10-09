using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Microsoft.Aspire;

// The shell's Home reads this: the composed modules joined with the AppHost's resource state.
internal static class InfrastructureEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        BrainRoutes.Group(endpoints, "/infrastructure").AddEndpointFilter(ModuleRouteGuard.Guard)
            .MapGet("", async ([FromServices] ModuleInventory inventory, [FromServices] IOptions<AspireOptions> options, IGrainFactory grains, CancellationToken ct) =>
            {
                var resources = await grains.GetGrain<IAspire>(options.Value.ApplicationName).ListResources(ct);
                return Results.Ok(InfrastructureView.Join(inventory.Modules.Select(module => module.Id.Value), resources));
            });
    }
}
