using DigitalBrain.Modules;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Circles;

// Legacy read-only circles. Existing circle brains stay readable at their stored neuron types for two releases;
// invitations return 410 Gone and device enrollment moved to the Devices module (see OWED.md).
[DigitalBrain.Modules.Module("circles")]
public sealed class CirclesModule : DigitalBrain.Modules.Module, IHttpModule
{
    internal const string GoneMessage = "Circles are now groups. Ask your friend for a friend code.";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(ILegacyCircle).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/circle-invitations/{brainId}/accept", Gone);
        endpoints.MapPost("/circle-invitations/{brainId}/codes", Gone);
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        DigitalBrain.Invocation.RuntimeProtocol.Register(CirclesJson.Default);
    }

    private static Task Gone(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status410Gone;
        http.Response.ContentType = "text/plain; charset=utf-8";
        return http.Response.WriteAsync(GoneMessage, http.RequestAborted);
    }
}
