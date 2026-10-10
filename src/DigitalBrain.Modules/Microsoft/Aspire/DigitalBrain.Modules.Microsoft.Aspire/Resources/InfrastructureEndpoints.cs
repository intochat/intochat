using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Microsoft.Aspire;

// The shell's Home reads this: the composed modules joined with the live cluster's silos.
internal static class InfrastructureEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        BrainRoutes.Group(endpoints, "/infrastructure").AddEndpointFilter(ModuleRouteGuard.Guard)
            .MapGet("", async ([FromServices] ModuleInventory inventory, IGrainFactory grains, CancellationToken ct) =>
            {
                var silos = await grains.GetGrain<ICluster>(ICluster.Key(BrainScope.CurrentId())).Silos(ct);
                return Results.Ok(InfrastructureView.Join(inventory.Modules.Select(module => module.Id.Value), silos));
            });
    }
}
