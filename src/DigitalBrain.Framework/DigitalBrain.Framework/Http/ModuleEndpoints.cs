using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Framework;

public static class ModuleEndpoints
{
    public static WebApplication MapDigitalBrainModules(this WebApplication app)
    {
        foreach (var module in app.Services.GetServices<Module>())
        {
            if (module is IHttpModule http) { http.Configure(app); }
        }
        return app;
    }
}
