using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections;

public static class HttpSurfaceExtensions
{
    public static IApplicationBuilder UseModuleHttpSurfaces(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        foreach (var surface in app.ApplicationServices.GetServices<IHttpSurface>())
        {
            surface.Map(app);
        }

        return app;
    }
}
