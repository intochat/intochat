using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Http;

namespace IntoChat.OS.Modules.Apps;

// Browse, inspect and install published apps from inside one brain.
internal static class MarketplaceEndpoints
{
    internal sealed record InstallBody(string? Consent, string? Name, Dictionary<string, string>? Bindings);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var marketplace = OsBrainRoutes.Group(endpoints, "/marketplace").AddEndpointFilter(ModuleRouteGuard.Guard);

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

        marketplace.MapPost("/{publisher}/{name}/install", (string publisher, string name, InstallBody? body, INeuronProxies brain, [FromServices] AppInstaller installer, CancellationToken ct)
            => Install(publisher, name, body, brain, installer, ct));
    }

    // The latest listing through the one install path: a missing or stale consent answers 409 with what the app asks for.
    internal static Task<IResult> Install(string publisher, string name, InstallBody? body, INeuronProxies brain, AppInstaller installer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
    {
        if (!AppsEndpoints.IsPerson(installer.Caller)) { return Results.Json(new { error = "Only you can install apps." }, statusCode: 403); }
        var outcome = await installer.Install(new(new(publisher, name), body?.Consent ?? "", Guid.Empty, body?.Name, body?.Bindings), ct);
        return outcome.Status switch
        {
            AppInstallStatus.Ok => Results.Ok(AppsEndpoints.View(await brain.Get<IApp>(IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(BrainScope.CurrentId()), outcome.InstalledName!).Key).Read().WaitAsync(ct))),
            AppInstallStatus.NeedsConsent => Results.Conflict(new { status = "needsConsent", requires = outcome.Requires, error = outcome.Message }),
            _ => AppsEndpoints.Refused(outcome),
        };
    });

    private static async Task<MarketplaceListing> Listing(MarketplaceView view, string publisher, string name, CancellationToken ct)
        => await view.Read(publisher, name, ct)
            ?? throw new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
