using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DigitalBrain.ServiceDefaults;

// The three probes every DigitalBrain host answers, and which checks each one runs:
//   /health  everything but the drain: the orchestrator's view, dependencies included.
//   /ready   the process itself (live) and the drain: what a load balancer probes. A dependency blip shared by
//            every instance must not take them all out of the balancer at once.
//   /alive   the process itself.
public static class HealthProbes
{
    public const string HealthPath = "/health";
    public const string ReadyPath = "/ready";
    public const string AlivePath = "/alive";
    public const string LiveTag = "live";
    public const string DrainTag = "drain";
    // How long a stopping instance keeps serving after /ready turns Unhealthy (Drain); unset, it stops at once.
    public const string DrainDelayKey = "DigitalBrain:Http:DrainDelay";

    public static bool Health(HealthCheckRegistration registration) => !Tagged(registration, DrainTag);

    public static bool Ready(HealthCheckRegistration registration) => Tagged(registration, LiveTag) || Tagged(registration, DrainTag);

    public static bool Alive(HealthCheckRegistration registration) => Tagged(registration, LiveTag);

    public static IEndpointRouteBuilder MapHealthProbes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHealthChecks(HealthPath, new HealthCheckOptions { Predicate = Health }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = Ready }).AllowAnonymous();
        endpoints.MapHealthChecks(AlivePath, new HealthCheckOptions { Predicate = Alive }).AllowAnonymous();
        return endpoints;
    }

    private static bool Tagged(HealthCheckRegistration registration, string tag) => registration.Tags.Contains(tag);
}
