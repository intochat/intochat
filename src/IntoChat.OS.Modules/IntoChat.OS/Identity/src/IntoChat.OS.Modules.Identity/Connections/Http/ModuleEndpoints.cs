using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Connections;

public static class ModuleEndpoints
{
    public static WebApplication MapIntoChatOSModules(this WebApplication app)
    {
        foreach (var http in app.Services.GetServices<IHttpModule>()) { http.Configure(app); }
        app.MapModuleHttpSurfaces().WithMetadata(IntoChat.OS.Authority.Identity.AccountSession.FrameworkSurface.Instance);
        return app;
    }
}
