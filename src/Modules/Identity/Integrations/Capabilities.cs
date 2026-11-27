using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Identity.Integrations;

internal sealed class Capabilities(IEnumerable<ICapabilitySource> sources) : ICapabilities
{
    public async Task<IntegrationCapability[]> List(string? kind = null, CancellationToken ct = default)
    {
        var derived = new List<IntegrationCapability>();
        foreach (var source in sources)
        {
            derived.AddRange(await source.Derive(ct));
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
        var group = BrainRouteGroups.Brain(endpoints, "/integrations");
        group.MapGet("/capabilities", async (string? kind, [FromServices] ICapabilities capabilities, CancellationToken cancellationToken) =>
            Results.Ok(await capabilities.List(kind, cancellationToken)));
    }
}
