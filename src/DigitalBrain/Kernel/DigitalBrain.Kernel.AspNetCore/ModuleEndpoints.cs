using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Kernel.AspNetCore;

public static class ModuleEndpoints
{
    public static WebApplication MapDigitalBrainModules(this WebApplication app)
    {
        // A composed host always answers for its own liveness; deployments health-check these
        // paths, so they are not optional plumbing a host can forget.
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();
        foreach (var module in app.Services.GetServices<IModule>())
        {
            if (module is IHttpModule http) { http.Configure(app); }
        }
        return app;
    }
}
