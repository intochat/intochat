using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using IntoChat.OS.Host.Http;

namespace IntoChat.OS.Authority.Integrations;

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
        var group = OsBrainRoutes.Group(endpoints, "/integrations");
        group.MapGet("/capabilities", async (string? kind, [FromServices] ICapabilities capabilities, CancellationToken cancellationToken) =>
            Results.Ok(await capabilities.List(kind, cancellationToken)));
    }
}
