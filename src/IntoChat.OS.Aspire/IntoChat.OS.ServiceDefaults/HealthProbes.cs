using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IntoChat.OS.ServiceDefaults;

// The three probes every IntoChat.OS host answers, and which checks each one runs:
//   /health  everything but the drain: the orchestrator's view, dependencies included.
//   /ready   the process itself (live) and the drain: what a load balancer probes. A dependency blip shared by
//            every instance must not take them all out of the balancer at once.
//   /alive   the process itself.
// and POST /drain, which a roll sends before it restarts the instance: /ready turns Unhealthy for good. Only a
// direct local caller may drain: a request through the front carries X-Forwarded-For and is refused.
public static class HealthProbes
{
    public const string HealthPath = IntoChat.OS.Contracts.Configuration.HostProbePaths.Health;
    public const string ReadyPath = IntoChat.OS.Contracts.Configuration.HostProbePaths.Ready;
    public const string AlivePath = IntoChat.OS.Contracts.Configuration.HostProbePaths.Alive;
    public const string DrainPath = "/drain";
    public const string LiveTag = "live";
    public const string DrainTag = "drain";
    // How long a stopping instance keeps serving after /ready turns Unhealthy (Drain); unset, it stops at once.
    public const string DrainDelayKey = "IntoChatOS:Http:DrainDelay";
    // Bounds the web server's own graceful stop after the drain: in-flight requests finish, new connections are refused.
    public static readonly TimeSpan ServerStop = TimeSpan.FromSeconds(5);

    public static bool Health(HealthCheckRegistration registration) => !Tagged(registration, DrainTag);

    public static bool Ready(HealthCheckRegistration registration) => Tagged(registration, LiveTag) || Tagged(registration, DrainTag);

    public static bool Alive(HealthCheckRegistration registration) => Tagged(registration, LiveTag);

    public static IEndpointRouteBuilder MapHealthProbes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHealthChecks(HealthPath, new HealthCheckOptions { Predicate = Health }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = Ready }).AllowAnonymous();
        endpoints.MapHealthChecks(AlivePath, new HealthCheckOptions { Predicate = Alive }).AllowAnonymous();
        endpoints.MapPost(DrainPath, (HttpContext context, Drain drain) =>
        {
            if (!MayDrain(context.Connection.RemoteIpAddress, context.Request.Headers.ContainsKey("X-Forwarded-For"))) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            drain.Begin();
            return Results.Accepted();
        }).AllowAnonymous();
        return endpoints;
    }

    public static bool MayDrain(IPAddress? remote, bool forwarded) => remote is not null && IPAddress.IsLoopback(remote) && !forwarded;

    private static bool Tagged(HealthCheckRegistration registration, string tag) => registration.Tags.Contains(tag);
}
