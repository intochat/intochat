using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Apps;

// Browse, inspect and install published apps from inside one brain.
internal static class MarketplaceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var marketplace = BrainRoutes.Group(endpoints, "/marketplace").AddEndpointFilter(ModuleRouteGuard.Guard);

        marketplace.MapGet("", ([FromServices] MarketplaceView view, [FromServices] AppInstaller installer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            var entries = new List<MarketplaceEntry>();
            foreach (var listing in await view.List(ct))
            { entries.Add(await installer.Entry(listing, ct)); }
            return Results.Ok(entries);
        }));

        marketplace.MapGet("/{publisher}/{name}", (string publisher, string name, [FromServices] MarketplaceView view, [FromServices] AppInstaller installer, CancellationToken ct)
            => AppsEndpoints.Respond(async () => Results.Ok(await installer.Entry(await Listing(view, publisher, name, ct), ct))));

        marketplace.MapGet("/{publisher}/{name}/icon", (string publisher, string name, [FromServices] MarketplaceView view, [FromServices] AppInstaller installer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            var listing = await Listing(view, publisher, name, ct);
            if (listing.Icon is null) { return Results.NotFound(new { error = $"{publisher}/{name} has no icon." }); }
            var bundle = await installer.Bundle(listing.Source, listing.Publisher, listing.Name, ct);
            return AppsEndpoints.Icon(listing.Icon, bundle.Files, $"{publisher}/{name}");
        }));

        marketplace.MapPost("/{publisher}/{name}/install", (string publisher, string name, [FromServices] AppInstaller installer, CancellationToken ct)
            => AppsEndpoints.Respond(async () => Results.Ok(AppsEndpoints.View(await installer.Install(publisher, name, ct)))));
    }

    private static async Task<MarketplaceListing> Listing(MarketplaceView view, string publisher, string name, CancellationToken ct)
        => await view.Read(publisher, name, ct)
            ?? throw new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
