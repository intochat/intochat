using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Framework;

public static class ModuleEndpoints
{
    public static WebApplication MapDigitalBrainModules(this WebApplication app)
    {
        // A composed host always answers for its own liveness; deployments health-check these
        // paths, so they are not optional plumbing a host can forget.
        // /ready adds the "drain" check (ServiceDefaults): a stopping instance leaves its load balancer first.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = static registration => !registration.Tags.Contains("drain"),
        }).AllowAnonymous();
        app.MapHealthChecks("/ready").AllowAnonymous();
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("live"),
        }).AllowAnonymous();
        foreach (var module in app.Services.GetServices<Module>())
        {
            if (module is IHttpModule http) { http.Configure(app); }
        }
        return app;
    }
}
