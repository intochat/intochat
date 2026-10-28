using CapabilityCaller = DigitalBrain.Capabilities.CapabilityCaller;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Integrations;

internal sealed class Capabilities(IEnumerable<ICapabilitySource> sources) : ICapabilities
{
    public async Task<Capability[]> List(CapabilityCaller caller, string? kind = null, CancellationToken ct = default)
    {
        var derived = new List<Capability>();
        foreach (var source in sources)
        {
            derived.AddRange(await source.Derive(caller, ct));
        }

        return [.. derived.Where(capability => kind is null || capability.Kind == kind)
            .OrderBy(capability => capability.IntegrationId, StringComparer.Ordinal)
            .ThenBy(capability => capability.Id, StringComparer.Ordinal)];
    }
}

internal static class CapabilityEndpoints
{
    internal static void MapCapabilities(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/integrations");
        group.MapGet("/capabilities", async (string? kind, [FromServices] ICapabilities capabilities, CancellationToken cancellationToken) =>
            Results.Ok(await capabilities.List(CallerContextStamper.Require(), kind, cancellationToken)));
    }
}
