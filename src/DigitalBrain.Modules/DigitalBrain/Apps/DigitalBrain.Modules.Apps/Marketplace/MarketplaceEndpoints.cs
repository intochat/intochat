using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Apps;

// Browse, inspect and install published apps from inside one brain.
internal static class MarketplaceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var marketplace = BrainRoutes.Group(endpoints, "/marketplace").AddEndpointFilter(ModuleRouteGuard.Guard);

        marketplace.MapGet("", (IDigitalBrain brain, [FromServices] AppInstaller installer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            var entries = new List<MarketplaceEntry>();
            foreach (var listing in await brain.Get<IMarketplace>(IMarketplace.Key).List().WaitAsync(ct))
            { entries.Add(await installer.Entry(listing, ct)); }
            return Results.Ok(entries);
        }));

        marketplace.MapGet("/{publisher}/{name}", (string publisher, string name, IDigitalBrain brain, [FromServices] AppInstaller installer, CancellationToken ct)
            => AppsEndpoints.Respond(async () => Results.Ok(await installer.Entry(await Listing(brain, publisher, name, ct), ct))));

        marketplace.MapGet("/{publisher}/{name}/icon", (string publisher, string name, IDigitalBrain brain, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            var listing = await Listing(brain, publisher, name, ct);
            if (listing.Icon is null) { return Results.NotFound(new { error = $"{publisher}/{name} has no icon." }); }
            var bundle = await brain.Get<IApp>(IApp.Key(listing.Source.RequireScope(), listing.Name))
                .ExportPublished().WaitAsync(ct);
            return AppsEndpoints.Icon(listing.Icon, bundle.Files, $"{publisher}/{name}");
        }));

        marketplace.MapPost("/{publisher}/{name}/install", (string publisher, string name, [FromServices] AppInstaller installer, CancellationToken ct)
            => AppsEndpoints.Respond(async () => Results.Ok(AppsEndpoints.View(await installer.Install(publisher, name, ct)))));
    }

    private static async Task<MarketplaceListing> Listing(IDigitalBrain brain, string publisher, string name, CancellationToken ct)
        => await brain.Get<IMarketplace>(IMarketplace.Key).Read(publisher, name).WaitAsync(ct)
            ?? throw new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
