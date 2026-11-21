using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Devices;

internal static class DevicesEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
    }
}
