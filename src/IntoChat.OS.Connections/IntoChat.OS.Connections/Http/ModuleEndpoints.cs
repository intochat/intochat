using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Connections;

public static class ModuleEndpoints
{
    public static WebApplication MapIntoChatOSModules(this WebApplication app)
    {
        foreach (var module in app.Services.GetServices<Module>())
        {
            if (module is IHttpModule http) { http.Configure(app); }
        }
        return app;
    }
}
