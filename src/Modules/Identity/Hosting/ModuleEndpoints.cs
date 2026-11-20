using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Identity.Hosting;

public static class ModuleEndpoints
{
    public static WebApplication MapIntoChatOSModules(this WebApplication app)
    {
        foreach (var http in app.Services.GetServices<IHttpModule>()) { http.Configure(app); }
        app.MapModuleHttpSurfaces().WithMetadata(IntoChat.OS.Modules.Identity.Accounts.AccountSession.FrameworkSurface.Instance);
        return app;
    }
}
